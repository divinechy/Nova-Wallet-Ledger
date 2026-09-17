using Microsoft.AspNetCore.Mvc;
using NovaWalletLedger.Api.Exceptions;

namespace NovaWalletLedger.Api.Middleware;

/// <summary>
/// Central place that turns any exception into a consistent RFC 7807 Problem Details
/// response, so callers never see a raw 500 stack trace or an inconsistent error shape.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Domain exception handled: {Title}", ex.Title);
            await WriteProblem(context, ex.StatusCode, ex.Title, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await WriteProblem(context, StatusCodes.Status500InternalServerError,
                "An unexpected error occurred", "Please retry; if the problem persists contact support.");
        }
    }

    private static async Task WriteProblem(HttpContext context, int statusCode, string title, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{statusCode}",
            Instance = context.Request.Path
        };

        if (context.TraceIdentifier is { Length: > 0 })
        {
            problem.Extensions["traceId"] = context.TraceIdentifier;
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(problem);
    }
}
