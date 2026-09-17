using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace NovaWalletLedger.Tests;

public class DailyLimitTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;

    public DailyLimitTests(WalletApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TransferExceedingDailyLimit_IsRejected()
    {
        var client = await _factory.CreateAuthorizedClientAsync("daily-limit-customer");
        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");

        // The test configuration sets DailyOutboundLimitKobo to 50,000,000 (₦500,000).
        // Fund the wallet well beyond that so the rejection is provably the daily-limit
        // rule, not an insufficient-funds rule.
        await client.CreditWalletAsync(fromWallet, 100_000_00);

        client.DefaultRequestHeaders.Add("Idempotency-Key", $"daily-limit-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers",
            new { toWalletId = toWallet, amountKobo = 50_000_01, reference = "over-limit" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("daily", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransferWithinDailyLimit_Succeeds()
    {
        var client = await _factory.CreateAuthorizedClientAsync("daily-limit-ok-customer");
        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");

        await client.CreditWalletAsync(fromWallet, 100_000_00);

        client.DefaultRequestHeaders.Add("Idempotency-Key", $"daily-limit-ok-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers",
            new { toWalletId = toWallet, amountKobo = 10_000_00, reference = "under-limit" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
