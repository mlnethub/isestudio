using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Authentication;
using OnToPilot.Authorization;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

// Local alias keeps the login / me endpoints calling
// <c>UserOut.From(user)</c> without the fully-qualified path; the
// canonical DTO lives in OnToPilot.Authentication so the dispatcher
// can share it without taking a dependency on the Controllers
// namespace (which would otherwise be a layering inversion:
// Integration → Controllers).
using UserOut = OnToPilot.Authentication.UserOut;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/auth</c> surface — login, logout, the
/// authenticated-user echo endpoint, and the admin-side user CRUD
/// operations. The Python backend exposes the same shape in
/// <c>backend/app/api/auth.py</c>, so existing client tooling keeps
/// working during the .NET migration.
/// </summary>
/// <remarks>
/// <para>
/// Login, logout, and <c>GET /me</c> are implemented in-line because the
/// Task 2 review flagged that the dispatcher-driven placeholder path
/// discarded the production sign-in flow: the existing
/// <see cref="Authentication.AuthenticationHandler"/>, the timing-safe
/// BCrypt verifier, and the session-cookie plumbing all live in this
/// controller's pre-existing tests (see
/// <c>src/OnToPilot.Tests/Authentication/AuthenticationContractTests.cs</c>).
/// Routing those through the dispatcher would orphan the
/// <see cref="AuthSessionEntity"/> persistence and the opaque-token
/// cookie issuance.
/// </para>
/// <para>
/// The admin-side user CRUD operations (list / create / update / delete)
/// still flow through <see cref="IIntegrationApiFacade"/> because those
/// payloads are populated by Stage 2/3 services that have not stabilised
/// yet — wiring them here would repeat the integration-by-controller
/// pattern the dispatcher exists to consolidate.
/// </para>
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly OnToPilotDbContext _db;
    private readonly OnToPilotOptions _options;
    private readonly IPasswordService _passwords;
    private readonly TimeProvider _clock;
    private readonly IIntegrationApiFacade _facade;
    private readonly LegacyIdAllocator _allocator;

    public AuthController(
        OnToPilotDbContext db,
        IOptions<OnToPilotOptions> options,
        IPasswordService passwords,
        TimeProvider clock,
        IIntegrationApiFacade facade,
        LegacyIdAllocator allocator)
    {
        _db = db;
        _options = options.Value;
        _passwords = passwords;
        _clock = clock;
        _facade = facade;
        _allocator = allocator;
    }

    /// <summary>
    /// Verify the credentials, create a server-side session, and set the
    /// opaque-token cookie. Both the failure message and the failure
    /// timing are deliberately constant — a missing username and a wrong
    /// password both pay the cost of one BCrypt round so an enum on the
    /// wire can't leak whether the username exists.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserOut), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest? body, CancellationToken ct)
    {
        // Even empty submissions must run the BCrypt round so the
        // response time matches a fully-populated bad request.
        if (body is null
            || string.IsNullOrWhiteSpace(body.Username)
            || string.IsNullOrEmpty(body.Password))
        {
            _passwords.Verify(string.Empty, PasswordService.TimingSafeDummyHash);
            return Unauthorized(new { detail = "Incorrect username or password" });
        }

        var user = await _db.Users
            .SingleOrDefaultAsync(u => u.Username == body.Username, ct)
            .ConfigureAwait(false);

        // Always invoke Verify — against a precomputed dummy hash when
        // the user is missing — so missing vs wrong-password both take
        // one BCrypt round. The booleans are then folded in without
        // short-circuiting.
        var presentedHash = user?.PasswordHash ?? PasswordService.TimingSafeDummyHash;
        var passwordOk = _passwords.Verify(body.Password, presentedHash);
        var ok = user is not null && user.Active && passwordOk;
        if (!ok)
        {
            return Unauthorized(new { detail = "Incorrect username or password" });
        }

        var token = CreateToken();
        var now = _clock.GetUtcNow();
        // Atomic alloc+save: holds the auth_session advisory lock until
        // COMMIT so a concurrent login request can't observe the same
        // MAX+1 and race on the UNIQUE(legacy_id) constraint.
        await _allocator.AllocateAndPersistAsync(new AuthSessionEntity
        {
            Token = token,
            UserId = user!.Id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(_options.SessionTtlHours),
        }, ct).ConfigureAwait(false);

        SetSessionCookie(token);
        return Ok(UserOut.From(user));
    }

    /// <summary>
    /// Drop the session row and clear the cookie. Safe to call without
    /// an active session — the response is always <c>{"ok": true}</c>.
    /// No <c>[Authorize]</c> attribute: the Python backend exposes this
    /// endpoint to anonymous callers (the cookie lookup is best-effort)
    /// and the existing contract test relies on the cookie being cleared
    /// even when no session row exists for the presented token.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> LogoutAsync(CancellationToken ct)
    {
        var cookieName = _options.SessionCookie;
        if (Request.Cookies.TryGetValue(cookieName, out var token)
            && !string.IsNullOrEmpty(token))
        {
            var session = await _db.AuthSessions
                .SingleOrDefaultAsync(s => s.Token == token, ct)
                .ConfigureAwait(false);
            if (session is not null)
            {
                _db.AuthSessions.Remove(session);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = _options.CookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            // Python backend emits max-age=0 to clear; match its wire
            // shape exactly so existing cookie-handling tooling keeps
            // working.
            MaxAge = TimeSpan.Zero,
        });
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Returns the authenticated user, or the same envelope-shaped 401
    /// the Python backend emits when no session is present. No
    /// <c>[Authorize]</c> attribute: the route has to answer with the
    /// envelope on its own so the response shape is identical to the
    /// upstream wire format.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserOut), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        if (HttpContext.Items.TryGetValue(Authentication.SessionAuthenticationHandler.UserItemKey, out var value)
            && value is UserEntity user)
        {
            return Ok(UserOut.From(user));
        }

        // Surface the framework-supplied failure reason if the auth
        // handler stashed one; otherwise fall back to "Not
        // authenticated" so the wire shape matches the FastAPI backend.
        var detail = HttpContext.Items.TryGetValue(Authentication.SessionAuthenticationHandler.FailDetailItemKey, out var fail)
            ? fail as string ?? "Not authenticated"
            : "Not authenticated";
        return Unauthorized(new { detail });
    }

    [HttpPatch("me")]
    [Authorize]
    public Task<IActionResult> UpdateMeAsync([FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync(
            "auth.update_me",
            new InternalRequest(null, null, null, null, ToBody(body), null, Actor),
            ct));

    [HttpGet("users")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> ListUsersAsync(CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync(
            "auth.list_users",
            new InternalRequest(null, null, null, null, null, null, Actor),
            ct));

    [HttpPost("users")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> CreateUserAsync([FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync(
            "auth.create_user",
            new InternalRequest(null, null, null, null, ToBody(body), null, Actor),
            ct));

    [HttpDelete("users/{uid}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> DeleteUserAsync(string uid, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync(
            "auth.delete_user",
            new InternalRequest(null, null, uid, null, null, null, Actor),
            ct));

    [HttpPatch("users/{uid}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> UpdateUserAsync(string uid, [FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync(
            "auth.update_user",
            new InternalRequest(null, null, uid, null, ToBody(body), null, Actor),
            ct));

    private void SetSessionCookie(string token)
    {
        Response.Cookies.Append(_options.SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = _options.CookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromHours(_options.SessionTtlHours),
        });
    }

    private static string CreateToken()
    {
        // 32 bytes of cryptographic randomness, base64url-encoded so it
        // fits cleanly inside an HttpOnly cookie value (and matches the
        // Python backend's `secrets.token_urlsafe(32)` length).
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // The dispatcher hands request.Actor.UserId down to services that
    // perform identity checks (e.g. AuthService.UpdateUserAsync rejects
    // "you can't deactivate yourself" by comparing the user being
    // mutated against the caller's id). AuthServices downstream parse
    // UserId back to Guid, so the controller MUST surface the real
    // UserEntity.Id — not a literal "system" — otherwise the guard
    // never fires and admins can lock themselves out.
    private Actor Actor =>
        HttpContext.Items.TryGetValue(Authentication.SessionAuthenticationHandler.UserItemKey, out var v) && v is UserEntity user
            ? new Actor(user.Id.ToString(), user.DisplayName ?? user.Username)
            : new Actor("anonymous");

    private static IReadOnlyDictionary<string, object?>? ToBody(object? body)
    {
        if (body is null) return null;
        if (body is IReadOnlyDictionary<string, object?> d) return d;
        // For anonymous / loose objects we just pass a wrapping dict
        // keyed by "_" so the dispatcher can decide how to consume the
        // body.
        return new Dictionary<string, object?> { ["_"] = body };
    }

    private async Task<IActionResult> OkEnvelope(Task<object?> call)
    {
        var result = await call.ConfigureAwait(false);
        return result is null ? Ok(new { ok = true }) : Ok(result);
    }
}

/// <summary>Login body posted by clients.</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
