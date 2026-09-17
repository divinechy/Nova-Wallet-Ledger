using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace NovaWalletLedger.Tests;

/// <summary>
/// Boots a real Postgres container (via Testcontainers) and the API in-process against it.
/// Using a real Postgres rather than EF Core's InMemory provider is deliberate: InMemory
/// doesn't honour transactions or row locks, so it would let the concurrency bugs this
/// suite exists to catch pass silently.
/// </summary>
public class WalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("novawallet_test")
        .WithUsername("novawallet")
        .WithPassword("novawallet_test_pw")
        .Build();

    public HttpClient AuthorizedClient(string customerId = "test-customer")
    {
        var client = CreateClient();
        return client;
    }

    public async Task<string> MintTokenAsync(HttpClient client, string customerId)
    {
        var response = await client.PostAsJsonAsync("/auth/dev-token", new { customerId });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DevTokenResponseDto>(TestHelpers.JsonOptions);
        return payload!.AccessToken;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _postgres.GetConnectionString(),
                ["DailyOutboundLimitKobo"] = "50000000"
            });
        });
    }

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await _postgres.StopAsync();
        await base.DisposeAsync();
    }
}

public record DevTokenResponseDto(string AccessToken, DateTimeOffset ExpiresAtUtc);
