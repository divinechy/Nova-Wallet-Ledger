namespace NovaWalletLedger.Api.Models;

public enum TransactionDirection
{
    Credit = 0,
    Debit = 1
}

public enum TransactionType
{
    Deposit = 0,
    TransferOut = 1,
    TransferIn = 2
}

/// <summary>
/// A single ledger entry on a wallet's statement. Newest-first, paginated.
/// </summary>
public class WalletTransaction
{
    public Guid Id { get; set; }
    public Guid WalletId { get; set; }
    public Guid? CounterpartyWalletId { get; set; }
    public TransactionDirection Direction { get; set; }
    public TransactionType Type { get; set; }
    public long AmountKobo { get; set; }
    public long BalanceAfterKobo { get; set; }
    public string? Reference { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
