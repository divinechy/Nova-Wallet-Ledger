using NovaWalletLedger.Api.Models;

namespace NovaWalletLedger.Api.Dtos;

public record CreateWalletRequest(string CustomerId);

public record WalletResponse(Guid Id, string CustomerId, long BalanceKobo, string Currency, DateTimeOffset CreatedAtUtc);

public record CreditWalletRequest(long AmountKobo, string? Reference);

public record TransferRequest(Guid ToWalletId, long AmountKobo, string? Reference);

public record TransactionResponse(
    Guid Id,
    Guid WalletId,
    Guid? CounterpartyWalletId,
    TransactionDirection Direction,
    TransactionType Type,
    long AmountKobo,
    long BalanceAfterKobo,
    string? Reference,
    DateTimeOffset CreatedAtUtc);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record DevTokenRequest(string CustomerId);

public record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
