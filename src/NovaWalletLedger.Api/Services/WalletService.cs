using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Api.Data;
using NovaWalletLedger.Api.Dtos;
using NovaWalletLedger.Api.Exceptions;
using NovaWalletLedger.Api.Models;

namespace NovaWalletLedger.Api.Services;

public class WalletService
{
    private readonly AppDbContext _db;
    private readonly long _dailyOutboundLimitKobo;
    private readonly TimeZoneInfo _watTimeZone;

    public WalletService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _dailyOutboundLimitKobo = config.GetValue<long?>("DailyOutboundLimitKobo") ?? 50_000_000;
        // West Africa Time is a fixed UTC+1 offset, no DST — modelled as a custom fixed-offset zone
        // so this works portably across platforms without relying on an IANA id being installed.
        _watTimeZone = TimeZoneInfo.CreateCustomTimeZone("WAT", TimeSpan.FromHours(1), "West Africa Time", "West Africa Time");
    }

    public async Task<Wallet> CreateWalletAsync(string customerId)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BalanceKobo = 0,
            Currency = "NGN",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        _db.AuditLogs.Add(new AuditLogEntry
        {
            WalletId = wallet.Id,
            Action = "WalletCreated",
            AmountKobo = null,
            BalanceAfterKobo = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        return wallet;
    }

    public async Task<Wallet> GetWalletAsync(Guid walletId)
    {
        var wallet = await _db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == walletId);
        return wallet ?? throw new WalletNotFoundException(walletId);
    }

    public async Task<PagedResponse<WalletTransaction>> GetStatementAsync(Guid walletId, int page, int pageSize)
    {
        var exists = await _db.Wallets.AsNoTracking().AnyAsync(w => w.Id == walletId);
        if (!exists) throw new WalletNotFoundException(walletId);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Transactions.AsNoTracking().Where(t => t.WalletId == walletId).OrderByDescending(t => t.CreatedAtUtc);
        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResponse<WalletTransaction>(items, page, pageSize, total);
    }

    /// <summary>
    /// Credits a wallet (e.g. simulating an inbound NIP transfer). Locks the single row for
    /// the duration of the DB transaction so a concurrent credit/transfer on the same wallet
    /// can't interleave with this update.
    /// </summary>
    public async Task<Wallet> CreditAsync(Guid walletId, long amountKobo, string? reference, string correlationId)
    {
        if (amountKobo <= 0) throw new InvalidAmountException("AmountKobo must be a positive integer number of kobo.");

        await using var tx = await _db.Database.BeginTransactionAsync();

        var wallet = await LockWalletForUpdateAsync(walletId);

        wallet.BalanceKobo += amountKobo;

        _db.Transactions.Add(new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = wallet.Id,
            CounterpartyWalletId = null,
            Direction = TransactionDirection.Credit,
            Type = TransactionType.Deposit,
            AmountKobo = amountKobo,
            BalanceAfterKobo = wallet.BalanceKobo,
            Reference = reference,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        _db.AuditLogs.Add(new AuditLogEntry
        {
            WalletId = wallet.Id,
            Action = "Credit",
            AmountKobo = amountKobo,
            BalanceAfterKobo = wallet.BalanceKobo,
            CorrelationId = correlationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return wallet;
    }

    /// <summary>
    /// Atomically moves funds between two wallets.
    ///
    /// Concurrency safety: both wallet rows are locked with SELECT ... FOR UPDATE inside a
    /// single DB transaction, always acquired in ascending wallet-id order. That fixed lock
    /// ordering is what prevents deadlocks when two transfers between the same pair of
    /// wallets race in opposite directions — without it, two concurrent transfers A→B and
    /// B→A could each hold one lock and wait forever for the other.
    ///
    /// Idempotency: the Idempotency-Key is claimed (inserted) inside the very same
    /// transaction as the balance mutation, so "record the key" and "move the money" either
    /// both happen or neither does. A unique constraint on the key column is the actual
    /// safety net if two requests with the same key race past the initial check.
    /// </summary>
    public async Task<(WalletTransaction? outboundTxn, int statusCode, object body)> TransferAsync(
        Guid fromWalletId,
        TransferRequest request,
        string idempotencyKey,
        string correlationId)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new IdempotencyKeyMissingException();
        if (request.AmountKobo <= 0) throw new InvalidAmountException("AmountKobo must be a positive integer number of kobo.");
        if (fromWalletId == request.ToWalletId) throw new SameWalletTransferException();

        var requestHash = ComputeRequestHash(fromWalletId, request);

        // First pass, outside a transaction: cheap short-circuit for the common replay case.
        var existing = await _db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Key == idempotencyKey);
        if (existing is not null)
        {
            return ReplayOrConflict(existing, requestHash, idempotencyKey);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Claim the key inside the transaction. If a concurrent request already claimed it
        // between our check above and here, the unique constraint on Key throws and we
        // fall back to reading whatever was actually persisted.
        try
        {
            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = idempotencyKey,
                RequestHash = requestHash,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync();
            var raced = await _db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.Key == idempotencyKey);
            return ReplayOrConflict(raced, requestHash, idempotencyKey);
        }

        var firstId = fromWalletId.CompareTo(request.ToWalletId) < 0 ? fromWalletId : request.ToWalletId;
        var secondId = firstId == fromWalletId ? request.ToWalletId : fromWalletId;

        var firstLocked = await LockWalletForUpdateAsync(firstId);
        var secondLocked = await LockWalletForUpdateAsync(secondId);

        var fromWallet = firstLocked.Id == fromWalletId ? firstLocked : secondLocked;
        var toWallet = firstLocked.Id == fromWalletId ? secondLocked : firstLocked;

        if (fromWallet.BalanceKobo < request.AmountKobo)
        {
            throw new InsufficientFundsException(fromWalletId);
        }

        var dailyTotal = await GetOutboundTotalSinceWatMidnightAsync(fromWalletId);
        if (dailyTotal + request.AmountKobo > _dailyOutboundLimitKobo)
        {
            throw new DailyLimitExceededException(_dailyOutboundLimitKobo);
        }

        fromWallet.BalanceKobo -= request.AmountKobo;
        toWallet.BalanceKobo += request.AmountKobo;

        var now = DateTimeOffset.UtcNow;

        var outboundTxn = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = fromWallet.Id,
            CounterpartyWalletId = toWallet.Id,
            Direction = TransactionDirection.Debit,
            Type = TransactionType.TransferOut,
            AmountKobo = request.AmountKobo,
            BalanceAfterKobo = fromWallet.BalanceKobo,
            Reference = request.Reference,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now
        };

        var inboundTxn = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = toWallet.Id,
            CounterpartyWalletId = fromWallet.Id,
            Direction = TransactionDirection.Credit,
            Type = TransactionType.TransferIn,
            AmountKobo = request.AmountKobo,
            BalanceAfterKobo = toWallet.BalanceKobo,
            Reference = request.Reference,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now
        };

        _db.Transactions.AddRange(outboundTxn, inboundTxn);

        _db.AuditLogs.AddRange(
            new AuditLogEntry
            {
                WalletId = fromWallet.Id,
                Action = "TransferOut",
                AmountKobo = request.AmountKobo,
                BalanceAfterKobo = fromWallet.BalanceKobo,
                CorrelationId = correlationId,
                Metadata = JsonSerializer.Serialize(new { toWallet = toWallet.Id, idempotencyKey }),
                CreatedAtUtc = now
            },
            new AuditLogEntry
            {
                WalletId = toWallet.Id,
                Action = "TransferIn",
                AmountKobo = request.AmountKobo,
                BalanceAfterKobo = toWallet.BalanceKobo,
                CorrelationId = correlationId,
                Metadata = JsonSerializer.Serialize(new { fromWallet = fromWallet.Id, idempotencyKey }),
                CreatedAtUtc = now
            });

        var responseBody = new TransactionResponse(
            outboundTxn.Id, outboundTxn.WalletId, outboundTxn.CounterpartyWalletId, outboundTxn.Direction,
            outboundTxn.Type, outboundTxn.AmountKobo, outboundTxn.BalanceAfterKobo, outboundTxn.Reference, outboundTxn.CreatedAtUtc);

        var record = await _db.IdempotencyRecords.SingleAsync(r => r.Key == idempotencyKey);
        record.ResponseStatusCode = StatusCodes.Status201Created;
        record.ResponseBody = JsonSerializer.Serialize(responseBody);
        record.CompletedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return (outboundTxn, StatusCodes.Status201Created, responseBody);
    }

    private async Task<Wallet> LockWalletForUpdateAsync(Guid walletId)
    {
        // FromSqlInterpolated + FOR UPDATE takes a row-level exclusive lock held until the
        // enclosing transaction commits or rolls back — this is what actually makes the
        // read-modify-write below safe under concurrent load, not just the check-then-act
        // in application code.
        var wallet = await _db.Wallets
            .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"Id\" = {walletId} FOR UPDATE")
            .SingleOrDefaultAsync();

        return wallet ?? throw new WalletNotFoundException(walletId);
    }

    private async Task<long> GetOutboundTotalSinceWatMidnightAsync(Guid walletId)
    {
        var nowWat = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _watTimeZone);
        var watMidnight = new DateTimeOffset(nowWat.Year, nowWat.Month, nowWat.Day, 0, 0, 0, nowWat.Offset);
        var utcCutoff = watMidnight.ToUniversalTime();

        return await _db.Transactions
            .Where(t => t.WalletId == walletId
                && t.Direction == TransactionDirection.Debit
                && t.Type == TransactionType.TransferOut
                && t.CreatedAtUtc >= utcCutoff)
            .SumAsync(t => (long?)t.AmountKobo) ?? 0;
    }

    private static string ComputeRequestHash(Guid fromWalletId, TransferRequest request)
    {
        var canonical = $"{fromWalletId}|{request.ToWalletId}|{request.AmountKobo}|{request.Reference}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    private static (WalletTransaction?, int, object) ReplayOrConflict(IdempotencyRecord record, string requestHash, string key)
    {
        if (record.RequestHash != requestHash)
        {
            throw new IdempotencyKeyConflictException(key);
        }

        if (record.ResponseBody is null || record.ResponseStatusCode is null)
        {
            // Another request with the same key is still mid-flight (claimed but not completed).
            throw new IdempotencyRequestInFlightException(key);
        }

        var body = JsonSerializer.Deserialize<TransactionResponse>(record.ResponseBody)!;
        return (null, record.ResponseStatusCode.Value, body);
    }
}
