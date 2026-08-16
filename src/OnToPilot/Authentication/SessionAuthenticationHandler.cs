using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Authentication;

/// <summary>
/// Custom <see cref="AuthenticationHandler{TOptions}"/> for OnToPilot's
/// opaque-token server-side sessions. Reads the configured cookie name (the
/// default <c>ontopilot_session</c> matches the Python backend), looks the
/// token up in <c>authsession</c>, and rejects expired or
/// owner-inactive sessions with the same wording the Python backend uses.
/// </summary>
/// <remarks>
/// <para>The handler consults <see cref="TimeProvider"/> for "now" so a fake
/// clock can be injected in unit tests to exercise the expiry path
/// deterministically.</para>
/// </remarks>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Cookie schema name. Registered globally via <c>AddAuthentication(...).AddScheme&lt;...&gt;(SessionAuthenticationDefaults.Scheme, ...)</c>.</summary>
    public const string SchemeName = "SessionCookie";

    /// <summary>HttpContext.Items key for the authenticated <see cref="UserEntity"/>, populated when a session is valid.</summary>
    public const string UserItemKey = "auth.user";

    /// <summary>HttpContext.Items key for the FastAPI-friendly failure detail (set on every non-success outcome).</summary>
    public const string FailDetailItemKey = "auth.failDetail";

    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<OnToPilotOptions> _optionsMonitor;

    /// <inheritdoc />
    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TimeProvider clock,
        IOptionsMonitor<OnToPilotOptions> optionsMonitor)
        : base(options, logger, encoder)
    {
        _clock = clock;
        _optionsMonitor = optionsMonitor;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = _optionsMonitor.CurrentValue.SessionCookie;
        var token = Request.Cookies[cookieName];
        if (string.IsNullOrEmpty(token))
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        // Resolve a scoped DbContext: the handler itself is a singleton.
        var db = Context.RequestServices.GetRequiredService<OnToPilotDbContext>();
        var session = await db.AuthSessions
            .SingleOrDefaultAsync(s => s.Token == token, Context.RequestAborted)
            .ConfigureAwait(false);

        if (session is null)
        {
            Context.Items[FailDetailItemKey] = "Not authenticated";
            return AuthenticateResult.NoResult();
        }

        var user = await db.Users
            .SingleOrDefaultAsync(u => u.Id == session.UserId, Context.RequestAborted)
            .ConfigureAwait(false);

        if (session.ExpiresAt <= _clock.GetUtcNow())
        {
            Context.Items[FailDetailItemKey] = "Session expired";
            return AuthenticateResult.Fail("Session expired");
        }

        if (user is null || !user.Active)
        {
            Context.Items[FailDetailItemKey] = "User inactive";
            return AuthenticateResult.Fail("User inactive");
        }

        Context.Items[UserItemKey] = user;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Translate the framework's 401 challenge into OnToPilot's envelope
    /// (<c>{"detail": "..."}</c>). Falls back to "Not authenticated" if the
    /// handler hasn't stashed a more specific reason.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var detail = ReadFailDetail() ?? "Not authenticated";
        await WriteEnvelopeAsync(StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>
    /// Translate the framework's 403 forbidden into OnToPilot's envelope.
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

/// <summary>Shared constants for <see cref="SessionAuthenticationHandler"/> consumers.</summary>
public static class SessionAuthenticationDefaults
{
    /// <summary>Claims principal name type, kept compatible with ASP.NET defaults.</summary>
    public const string PrincipalName = "OnToPilot";

    /// <summary>Convenience re-export of the handler scheme name.</summary>
    public const string Scheme = SessionAuthenticationHandler.SchemeName;
}
