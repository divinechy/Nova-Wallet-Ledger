namespace NovaWalletLedger.Api.Models;

/// <summary>
/// A customer's NovaWallet. Balance is always stored in kobo (integer, NGN minor unit)
/// to avoid floating point drift in the money path.
/// </summary>
public class Wallet
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = default!;
    public long BalanceKobo { get; set; }
    public string Currency { get; set; } = "NGN";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
