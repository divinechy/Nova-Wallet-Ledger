using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace NovaWalletLedger.Tests;

public class ConcurrencyTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;

    public ConcurrencyTests(WalletApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The core hard constraint from the brief: fire many concurrent transfer requests
    /// against the same source wallet for more money, in total, than it holds, and verify
    /// the balance never goes negative and exactly as many transfers succeed as the
    /// balance actually allows — no double-spend, no lost updates.
    /// </summary>
    [Fact]
    public async Task ConcurrentTransfers_NeverAllowNegativeBalance_OrDoubleSpend()
    {
        var client = await _factory.CreateAuthorizedClientAsync("concurrency-test-customer");

        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");

        const long startingBalance = 10_000_00; // ₦10,000.00 in kobo
        const long amountPerTransfer = 500_00;  // ₦500.00 in kobo
        const int concurrentRequests = 40;      // requests for 20x the wallet's actual balance

        await client.CreditWalletAsync(fromWallet, startingBalance);

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            using var requestClient = await _factory.CreateAuthorizedClientAsync("concurrency-test-customer");
            requestClient.DefaultRequestHeaders.Add("Idempotency-Key", $"concurrency-test-{i}-{Guid.NewGuid()}");

            var request = new { toWalletId = toWallet, amountKobo = amountPerTransfer, reference = $"concurrent-{i}" };
            return await requestClient.PostAsJsonAsync($"/wallets/{fromWallet}/transfers", request);
        });

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var insufficientFundsCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        // Every response must be an explicit success or an explicit business rejection —
        // never a 500, a timeout-shaped failure, or anything that would suggest a lost or
        // corrupted update.
        Assert.Equal(concurrentRequests, successCount + insufficientFundsCount);

        var expectedSuccessCount = (int)(startingBalance / amountPerTransfer);
        Assert.Equal(expectedSuccessCount, successCount);

        var finalFromWallet = await client.GetWalletAsync(fromWallet);
        var finalToWallet = await client.GetWalletAsync(toWallet);

        Assert.True(finalFromWallet.BalanceKobo >= 0, "Balance must never go negative.");
        Assert.Equal(startingBalance - (successCount * amountPerTransfer), finalFromWallet.BalanceKobo);
        Assert.Equal(successCount * amountPerTransfer, finalToWallet.BalanceKobo);
    }

    [Fact]
    public async Task ConcurrentCredits_ToSameWallet_AreAllApplied()
    {
        var client = await _factory.CreateAuthorizedClientAsync("concurrent-credit-customer");
        var wallet = await client.CreateWalletAsync("payee-only");

        const int concurrentCredits = 30;
        const long amountPerCredit = 100_00;

        var tasks = Enumerable.Range(0, concurrentCredits).Select(async _ =>
        {
            using var requestClient = await _factory.CreateAuthorizedClientAsync("concurrent-credit-customer");
            return await requestClient.PostAsJsonAsync($"/wallets/{wallet}/credit", new { amountKobo = amountPerCredit, reference = "concurrent-credit" });
        });

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var finalWallet = await client.GetWalletAsync(wallet);
        Assert.Equal(concurrentCredits * amountPerCredit, finalWallet.BalanceKobo);
    }
}
