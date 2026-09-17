using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace NovaWalletLedger.Tests;

public class IdempotencyTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;

    public IdempotencyTests(WalletApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ReplayingSameIdempotencyKey_DoesNotDoubleProcess()
    {
        var client = await _factory.CreateAuthorizedClientAsync("idempotency-customer");
        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");
        await client.CreditWalletAsync(fromWallet, 100_00);

        var key = $"replay-test-{Guid.NewGuid()}";
        var request = new { toWalletId = toWallet, amountKobo = 30_00, reference = "replay" };

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers", request);
        var second = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody);

        var finalFromWallet = await client.GetWalletAsync(fromWallet);
        Assert.Equal(70_00, finalFromWallet.BalanceKobo); // only debited once
    }

    [Fact]
    public async Task ReusingKey_WithDifferentPayload_IsRejected()
    {
        var client = await _factory.CreateAuthorizedClientAsync("idempotency-conflict-customer");
        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");
        await client.CreditWalletAsync(fromWallet, 100_00);

        var key = $"conflict-test-{Guid.NewGuid()}";

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers",
            new { toWalletId = toWallet, amountKobo = 10_00, reference = "first-payload" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers",
            new { toWalletId = toWallet, amountKobo = 20_00, reference = "different-payload" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task MissingIdempotencyKey_IsRejected()
    {
        var client = await _factory.CreateAuthorizedClientAsync("idempotency-missing-customer");
        var fromWallet = await client.CreateWalletAsync("payer");
        var toWallet = await client.CreateWalletAsync("payee");
        await client.CreditWalletAsync(fromWallet, 100_00);

        var response = await client.PostAsJsonAsync($"/wallets/{fromWallet}/transfers",
            new { toWalletId = toWallet, amountKobo = 10_00, reference = "no-key" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
