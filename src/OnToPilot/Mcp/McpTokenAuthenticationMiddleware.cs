using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Mcp;

/// <summary>
/// MCP transport authentication middleware. Parses the bearer token from
/// the <c>Authorization</c> header, looks up the SHA-256 hash in
/// <c>mcp_user_tokens</c>, validates that the row is active, the bound
/// user is active, and the bound knowledge system still exists, and
/// stamps the resulting <see cref="McpPrincipal"/> on
/// <see cref="HttpContext.Items"/>.
///
/// <para>Routes that do not start with <c>/mcp</c> pass through
/// untouched — the internal REST controllers already authenticate via
/// the session / bearer schemes registered in <c>Program.cs</c>.</para>
///
/// <para>On any failure the middleware writes a FastAPI-style
/// <c>{"detail": "..."}</c> envelope and short-circuits the pipeline.
/// Successful authentication leaves the response alone and lets the
/// next middleware run.</para>
/// </summary>
public sealed class McpTokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Path prefix the middleware applies to.</summary>
    public const string PathPrefix = "/mcp";

    /// <summary>DI constructor.</summary>
    public McpTokenAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Standard ASP.NET Core middleware entry point. Routes that are not
    /// MCP endpoints short-circuit straight to <c>await _next(context)</c>;
    /// the middleware is a no-op for every other path.
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        IMcpTokenService tokens,
        ILogger<McpTokenAuthenticationMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Pass through anything that isn't an MCP endpoint. The
        // middleware is registered globally so it can run ahead of the
        // routing layer; checking the path keeps the cost on unrelated
        // routes at one string comparison.
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Only enforce auth on the JSON-RPC POST. The MCP SDK handles
        // GET (notifications / streaming) and the Streamable HTTP
        // handshake; if the SDK ever emits a GET to /mcp the spec
        // permits letting it through without a bearer so the client can
        // receive the "401 with WWW-Authenticate" challenge.
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var plaintext = ExtractBearer(context.Request.Headers.Authorization);
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            await RejectAsync(context, "Not authenticated").ConfigureAwait(false);
            return;
        }

        var verification = await tokens.VerifyAsync(plaintext, context.RequestAborted).ConfigureAwait(false);
        if (verification is null)
        {
            logger.LogDebug("MCP bearer verification failed for prefix {Prefix}",
                plaintext[..Math.Min(8, plaintext.Length)]);
            await RejectAsync(context, "Not authenticated").ConfigureAwait(false);
            return;
        }

        context.Items[McpPrincipalAccessor.PlaintextItemKey] = plaintext;
        context.Items[McpPrincipalAccessor.PrincipalItemKey] = new McpPrincipal(
            User: verification.User,
            KnowledgeSystem: verification.KnowledgeSystem,
            Scopes: verification.Token.Scopes);
        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Pull the bearer token out of the <c>Authorization</c> header.
    /// Returns <c>null</c> for missing headers and non-bearer schemes so
    /// the caller can emit the same 401 envelope regardless of which
    /// form the failure took.
    /// </summary>
    private static string? ExtractBearer(Microsoft.Extensions.Primitives.StringValues header)
    {
        if (header.Count == 0) return null;
        var raw = header.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        const string scheme = "Bearer ";
        if (!raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        return raw[scheme.Length..].Trim();
    }

    private static async Task RejectAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { detail });
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}