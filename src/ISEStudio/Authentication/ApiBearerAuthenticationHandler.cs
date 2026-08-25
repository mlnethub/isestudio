using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ISEStudio.Authentication;

/// <summary>
/// ASP.NET Core authentication scheme that accepts <c>Authorization: Bearer &lt;token&gt;</c>
/// headers for the external read-only API. Looks the plaintext up by hashing
/// it (SHA-256) and matching the resulting digest against
/// <c>knowledgeapitoken</c>.<c>TokenHash</c>. The plaintext is never persisted,
/// so the only way to authenticate is to present the original bearer secret.
/// </summary>
/// <remarks>
/// <para>On success the handler stores the verification result on
/// <c>HttpContext.Items</c> under <see cref="VerificationItemKey"/> so the
/// resource endpoint can recover the matched knowledge system without a
/// second DB round-trip.</para>
/// <para>The token service is resolved per-request from
/// <see cref="HttpContext.RequestServices"/> so the handler's DI-graph
/// capture matches <see cref="SessionAuthenticationHandler"/>; this keeps
/// the lifetime story symmetric when a parallel MCP-bearer scheme is
/// added in Stage 4.</para>
/// <para>Errors are written into the FastAPI <c>{"detail": ...}</c> envelope
/// to stay consistent with the rest of the API.</para>
/// </remarks>
public sealed class ApiBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Scheme name. Register via <c>AddScheme&lt;..., ApiBearerAuthenticationHandler&gt;</c>.</summary>
    public const string SchemeName = "ApiBearer";

    /// <summary>
    /// HttpContext.Items key for the verified
    /// <see cref="KnowledgeApiTokenVerificationResult"/>. Endpoints guarded by
    /// this scheme read this item to access the matched token + KS.
    /// </summary>
    public const string VerificationItemKey = "auth.bearerVerification";

    /// <summary>HttpContext.Items key for the FastAPI-friendly failure detail.</summary>
    public const string FailDetailItemKey = "auth.bearerFailDetail";

    /// <inheritdoc />
    public ApiBearerAuthenticationHandler(
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
        // DI graph stays free of scoped dependencies (matches the pattern
        // used by SessionAuthenticationHandler).
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
            new(ClaimTypes.Name, $"opk-token:{verification.Token.Id}"),
            new("knowledge_system_id", verification.KnowledgeSystem.Id.ToString()),
            new("knowledge_system_public_id", verification.KnowledgeSystem.PublicId),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Translate the framework's 401 challenge into ISEStudio's envelope
    /// (<c>{"detail": "..."}</c>) so failed bearer requests look the same as
    /// failed session requests.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var detail = ReadFailDetail() ?? "Not authenticated";
        await WriteEnvelopeAsync(StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>
    /// Translate the framework's 403 forbidden into ISEStudio's envelope.
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