using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OnToPilot.Api;

/// <summary>
/// Translates every error response into FastAPI's <c>{"detail": ...}</c>
/// envelope. The default ASP.NET Core pipeline exposes
/// <c>application/problem+json</c> for unhandled routes, model validation
/// failures, and exception handlers; the Python backend emits <c>{"detail":
/// "..."}</c> instead and our existing client tooling depends on that shape.
/// </summary>
/// <remarks>
/// <para>The middleware handles three failure shapes:</para>
/// <list type="number">
///   <item>Unhandled exceptions → HTTP 500 <c>{"detail": "Internal server error"}</c> (no stack trace is ever leaked).</item>
///   <item>Empty 4xx responses (e.g. unmatched routes) → envelope with a status-appropriate message.</item>
/// </list>
/// <para>Per-endpoint 401/403/404 details are emitted by the controller /
/// <see cref="Authentication.SessionAuthenticationHandler"/> when the
/// reason is known; this middleware only fills the holes.</para>
/// </remarks>
public sealed class FastApiErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FastApiErrorMiddleware> _logger;

    public FastApiErrorMiddleware(RequestDelegate next, ILogger<FastApiErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in OnToPilot pipeline");
            await WriteEnvelopeAsync(context, StatusCodes.Status500InternalServerError, "Internal server error")
                .ConfigureAwait(false);
            return;
        }

        // If the inner pipeline produced a 4xx/5xx with no body (an
        // unmatched API route, or a misuse middleware that didn't write),
        // fill it with our envelope so clients see the expected shape.
        if (!context.Response.HasStarted
            && context.Response.StatusCode >= 400
            && context.Response.ContentLength is null or 0)
        {
            var detail = DetailFor(context.Response.StatusCode);
            await WriteEnvelopeAsync(context, context.Response.StatusCode, detail)
                .ConfigureAwait(false);
        }
    }

    private static string DetailFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Not authenticated",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        _ => $"HTTP {status}",
    };

    private static async Task WriteEnvelopeAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { detail });
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
