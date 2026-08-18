using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OnToPilot.Authentication;

/// <summary>
/// ASP.NET Core authentication scheme that accepts
/// <c>Authorization: Bearer &lt;token&gt;</c> for the external
/// (<c>/api/v1/knowledge-systems/{public_id}/...</c>) read-only API.
/// Hashes the presented plaintext with SHA-256 and matches the digest
/// against <c>knowledgeapitoken.TokenHash</c> — the plaintext is never
/// persisted, so the only way to authenticate is to present the original
/// bearer secret.
///
/// <para>Distinct from <see cref="ApiBearerAuthenticationHandler"/>: the
/// two handlers both verify the same <see cref="KnowledgeApiTokenEntity"/>
/// rows, but the external scheme is wired to the <c>/api/v1</c> surface
/// only (the brief mandates the external token handler is separate and
/// scoped to that path), and only the external scheme surfaces the
/// <c>WWW-Authenticate: Bearer</c> response header that RFC 6750
/// requires for 401s on bearer-token-protected resources.</para>
/// </summary>
/// <remarks>
/// <para>On success the handler stashes the verification result on
/// <see cref="HttpContext.Items"/> under
/// <see cref="VerificationItemKey"/>, and exposes the same fields as
/// <c>TokenPrincipal</c> (<c>token_id</c>, <c>knowledge_system_public_id</c>,
/// scope list) as <see cref="Claim"/>s on the
/// <see cref="ClaimsPrincipal"/>.</para>
/// <para>The token service is resolved per-request from
/// <see cref="HttpContext.RequestServices"/> so the handler's DI-graph
/// capture stays free of scoped dependencies (the same pattern
/// <see cref="ApiBearerAuthenticationHandler"/> uses).</para>
/// <para>Errors are written into the FastAPI
/// <c>{"detail": ...}</c> envelope to stay consistent with the rest of
/// the API.</para>
/// </remarks>
public sealed class ExternalTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Scheme name. Register via <c>AddScheme&lt;..., ExternalTokenAuthenticationHandler&gt;</c>.</summary>
    public const string SchemeName = "ExternalToken";

    /// <summary>
    /// HttpContext.Items key for the verified
    /// <see cref="KnowledgeApiTokenVerificationResult"/>. Endpoints
    /// guarded by this scheme read this item to access the matched
    /// token + KS + scopes without a second DB round-trip.
    /// </summary>
    public const string VerificationItemKey = "auth.externalVerification";

    /// <summary>HttpContext.Items key for the FastAPI-friendly failure detail.</summary>
    public const string FailDetailItemKey = "auth.externalFailDetail";

    /// <inheritdoc />
    public ExternalTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        var raw = authHeader.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        // Expect "Bearer <token>" (RFC 6750). Reject anything else cleanly.
        const string bearerScheme = "Bearer ";
        if (!raw.StartsWith(bearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        var plaintext = raw[bearerScheme.Length..].Trim();
        if (plaintext.Length == 0)
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        // Resolve the token service per-request so the handler's captured
        // DI graph stays free of scoped dependencies.
        var tokens = Context.RequestServices.GetRequiredService<IKnowledgeApiTokenService>();

        // Verify through the token service so we don't duplicate the
        // hash + active-row lookup logic.
        var verification = await tokens.VerifyAsync(plaintext, Context.RequestAborted)
            .ConfigureAwait(false);
        if (verification is null)
        {
            Context.Items[FailDetailItemKey] = "Invalid or expired API token";
            return AuthenticateResult.Fail("Invalid or expired API token");
        }

        Context.Items[VerificationItemKey] = verification;
        var claims = new List<Claim>
        {
            // The principal name is the token id — the brief requires
            // the scope / status / role checks run live on every call,
            // so the principal is intentionally a thin handle onto the
            // verified row, not a snapshot of the user.
            new(ClaimTypes.Name, $"external-token:{verification.Token.Id}"),
            new("token_id", verification.Token.Id.ToString()),
            new("knowledge_system_id", verification.KnowledgeSystem.Id.ToString()),
            new("knowledge_system_public_id", verification.KnowledgeSystem.PublicId),
        };
        foreach (var scope in verification.Token.Scopes)
        {
            claims.Add(new Claim("scope", scope));
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Translate the framework's 401 challenge into OnToPilot's envelope
    /// (<c>{"detail": "..."}</c>) and stamp
    /// <c>WWW-Authenticate: Bearer realm="ontopilot"</c> on the
    /// response — RFC 6750 mandates the header for bearer-token
    /// protected resources so clients can detect the auth scheme.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var detail = ReadFailDetail() ?? "Not authenticated";
        Response.Headers["WWW-Authenticate"] = "Bearer realm=\"ontopilot\"";
        await WriteEnvelopeAsync(StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>
    /// Translate the framework's 403 forbidden into OnToPilot's envelope.
    /// The external / published surface uses 403 for "token valid but
    /// scope insufficient" so the response stays compatible with the
    /// Python backend.
    /// </summary>
    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var detail = ReadFailDetail() ?? "Forbidden";
        await WriteEnvelopeAsync(StatusCodes.Status403Forbidden, detail);
    }

    private string? ReadFailDetail() =>
        Context.Items.TryGetValue(FailDetailItemKey, out var v) ? v as string : null;

    private async Task WriteEnvelopeAsync(int statusCode, string detail)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json; charset=utf-8";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { detail });
        await Response.Body.WriteAsync(body, Context.RequestAborted);
    }
}
