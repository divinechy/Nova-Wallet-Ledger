using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NovaWalletLedger.Tests;

public static class TestHelpers
{
    // The API serializes with ASP.NET Core's default (camelCase) JSON options; System.Net.Http.Json's
    // ReadFromJsonAsync defaults to case-sensitive matching, so tests need this explicitly to avoid
    // silently deserializing into null/default properties.
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<HttpClient> CreateAuthorizedClientAsync(this WalletApiFactory factory, string customerId)
    {
        var bootstrapClient = factory.CreateClient();
        var token = await factory.MintTokenAsync(bootstrapClient, customerId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<Guid> CreateWalletAsync(this HttpClient client, string customerId)
    {
        var response = await client.PostAsJsonAsync("/wallets", new { customerId });
        response.EnsureSuccessStatusCode();
        var wallet = await response.Content.ReadFromJsonAsync<WalletDto>(JsonOptions);
        return wallet!.Id;
    }

    public static async Task CreditWalletAsync(this HttpClient client, Guid walletId, long amountKobo)
    {
        var response = await client.PostAsJsonAsync($"/wallets/{walletId}/credit", new { amountKobo, reference = "test-seed" });
        response.EnsureSuccessStatusCode();
    }

    public static async Task<WalletDto> GetWalletAsync(this HttpClient client, Guid walletId)
    {
        var response = await client.GetAsync($"/wallets/{walletId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WalletDto>(JsonOptions))!;
    }
}

public record WalletDto(Guid Id, string CustomerId, long BalanceKobo, string Currency, DateTimeOffset CreatedAtUtc);
