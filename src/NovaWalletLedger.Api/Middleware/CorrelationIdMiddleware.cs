namespace NovaWalletLedger.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation id (accepting one supplied by the caller via
/// X-Correlation-Id, generating one otherwise), echoes it back on the response, and pushes
/// it into the logging scope so every log line for this request can be tied together.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}

public static class HttpContextCorrelationExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue("CorrelationId", out var value) && value is string s ? s : "unknown";
}
