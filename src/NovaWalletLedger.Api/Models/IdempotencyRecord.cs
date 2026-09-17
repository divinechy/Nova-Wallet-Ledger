namespace NovaWalletLedger.Api.Models;

/// <summary>
/// Tracks the outcome of a request made with an Idempotency-Key so replays return the
/// original response instead of re-processing, and a key reused with a different payload
/// is rejected.
/// </summary>
public class IdempotencyRecord
{
    public string Key { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
