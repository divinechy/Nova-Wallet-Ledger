namespace NovaWalletLedger.Api.Models;

/// <summary>
/// Append-only, immutable audit trail. Written for every balance mutation, kept separate
/// from the transaction/statement table so it can be queried independently and is never
/// updated or deleted by application code.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }
    public Guid WalletId { get; set; }
    public string Action { get; set; } = default!;
    public long? AmountKobo { get; set; }
    public long BalanceAfterKobo { get; set; }
    public string? CorrelationId { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
