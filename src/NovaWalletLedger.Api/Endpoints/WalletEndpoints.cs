using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWalletLedger.Api.Data;
using NovaWalletLedger.Api.Dtos;
using NovaWalletLedger.Api.Middleware;
using NovaWalletLedger.Api.Services;

namespace NovaWalletLedger.Api.Endpoints;

public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var wallets = app.MapGroup("/wallets").RequireAuthorization().WithTags("Wallets");

        wallets.MapPost("/", async ([FromBody] CreateWalletRequest request, WalletService service) =>
        {
            var wallet = await service.CreateWalletAsync(request.CustomerId);
            var response = new WalletResponse(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency, wallet.CreatedAtUtc);
            return Results.Created($"/wallets/{wallet.Id}", response);
        })
        .WithSummary("Create a wallet for a customer, starting balance zero.");

        wallets.MapGet("/{walletId:guid}", async (Guid walletId, WalletService service) =>
        {
            var wallet = await service.GetWalletAsync(walletId);
            return Results.Ok(new WalletResponse(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency, wallet.CreatedAtUtc));
        })
        .WithSummary("Get current balance and currency for a wallet.");

        wallets.MapPost("/{walletId:guid}/credit", async (Guid walletId, [FromBody] CreditWalletRequest request, WalletService service, HttpContext ctx) =>
        {
            var wallet = await service.CreditAsync(walletId, request.AmountKobo, request.Reference, ctx.GetCorrelationId());
            return Results.Ok(new WalletResponse(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency, wallet.CreatedAtUtc));
        })
        .WithSummary("Deposit funds into a wallet, simulating an inbound NIP transfer.");

        wallets.MapPost("/{walletId:guid}/transfers", async (
                Guid walletId,
                [FromBody] TransferRequest request,
                WalletService service,
                HttpContext ctx) =>
            {
                var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].ToString();
                var (_, statusCode, body) = await service.TransferAsync(walletId, request, idempotencyKey, ctx.GetCorrelationId());
                return Results.Json(body, statusCode: statusCode);
            })
            .WithSummary("Atomically transfer funds between two wallets. Requires an Idempotency-Key header.")
            .RequireRateLimiting("transfer");

        wallets.MapGet("/{walletId:guid}/statement", async (Guid walletId, [FromQuery] int page, [FromQuery] int pageSize, WalletService service) =>
        {
            var effectivePageSize = pageSize == 0 ? 20 : pageSize;
            var effectivePage = page == 0 ? 1 : page;
            var result = await service.GetStatementAsync(walletId, effectivePage, effectivePageSize);

            var items = result.Items.Select(t => new TransactionResponse(
                t.Id, t.WalletId, t.CounterpartyWalletId, t.Direction, t.Type, t.AmountKobo, t.BalanceAfterKobo, t.Reference, t.CreatedAtUtc));

            return Results.Ok(new PagedResponse<TransactionResponse>(items.ToList(), result.Page, result.PageSize, result.TotalCount));
        })
        .WithSummary("Paginated transaction history for a wallet, newest first.");
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/dev-token", (DevTokenRequest request, DevTokenService tokenService) =>
        {
            var (token, expires) = tokenService.IssueToken(request.CustomerId);
            return Results.Ok(new DevTokenResponse(token, expires));
        })
        .WithTags("Auth")
        .AllowAnonymous()
        .WithSummary("DEV ONLY: mints a bearer token for a customer id, standing in for a real identity provider.");
    }

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous()
            .WithTags("Health")
            .ExcludeFromDescription();

        app.MapGet("/health/ready", async (AppDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        })
        .AllowAnonymous()
        .WithTags("Health")
        .ExcludeFromDescription();
    }
}
