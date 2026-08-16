using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnToPilot.Authentication;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Controllers;

/// <summary>
/// Login, logout, and current-user endpoints. Matches the public surface
/// shape of the Python backend's <c>backend/app/api/auth.py</c> so existing
/// client tooling keeps working during the .NET migration.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly OnToPilotDbContext _db;
    private readonly OnToPilotOptions _options;
    private readonly IPasswordService _passwords;
    private readonly TimeProvider _clock;

    public AuthController(
        OnToPilotDbContext db,
        IOptions<OnToPilotOptions> options,
        IPasswordService passwords,
        TimeProvider clock)
    {
        _db = db;
        _options = options.Value;
        _passwords = passwords;
        _clock = clock;
    }

    /// <summary>
    /// Verify the credentials, create a server-side session, and set the
    /// opaque-token cookie. Both the failure message and the failure timing
    /// are deliberately constant — a missing username and a wrong password
    /// both pay the cost of one BCrypt round so an enum on the wire can't
    /// leak whether the username exists.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(UserOut), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
        {
            // Even empty submissions must run the BCrypt round so the
            // response time matches a fully-populated bad request.
            _passwords.Verify(string.Empty, PasswordService.TimingSafeDummyHash);
            return Unauthorized(new { detail = "Incorrect username or password" });
        }

        var user = await _db.Users
            .SingleOrDefaultAsync(u => u.Username == body.Username, ct)
            .ConfigureAwait(false);

        // Always invoke Verify — against a precomputed dummy hash when the
        // user is missing — so missing vs wrong-password both take one BCrypt
        // round. The booleans are then folded in without short-circuiting.
        var presentedHash = user?.PasswordHash ?? PasswordService.TimingSafeDummyHash;
        var passwordOk = _passwords.Verify(body.Password, presentedHash);
        var ok = user is not null && user.Active && passwordOk;
        if (!ok) return Unauthorized(new { detail = "Incorrect username or password" });

        var token = CreateToken();
        var now = _clock.GetUtcNow();
        _db.AuthSessions.Add(new AuthSessionEntity
        {
            Token = token,
            UserId = user!.Id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(_options.SessionTtlHours),
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        SetSessionCookie(token);
        return Ok(UserOut.From(user));
    }

    /// <summary>
    /// Drop the session row and clear the cookie. Safe to call without an
    /// active session — the response is always <c>{"ok": true}</c>.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> LogoutAsync(CancellationToken ct)
    {
        var cookieName = _options.SessionCookie;
        if (Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrEmpty(token))
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
            // Python backend emits max-age=0 to clear; match its wire shape
            // exactly so existing cookie-handling tooling keeps working.
            MaxAge = TimeSpan.Zero,
        });
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Returns the authenticated user, or the same envelope-shaped 401 the
    /// Python backend emits when no session is present.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserOut), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        if (HttpContext.Items.TryGetValue(SessionAuthenticationHandler.UserItemKey, out var value)
            && value is UserEntity user)
        {
            return Ok(UserOut.From(user));
        }

        var detail = HttpContext.Items.TryGetValue(SessionAuthenticationHandler.FailDetailItemKey, out var fail)
            ? fail as string ?? "Not authenticated"
            : "Not authenticated";
        return Unauthorized(new { detail });
    }

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
        // 32 bytes of cryptographic randomness, base64url-encoded so it fits
        // cleanly inside an HttpOnly cookie value (and matches the Python
        // backend's `secrets.token_urlsafe(32)` length).
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>Login body posted by clients.</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>Public user view. Mirrors the Python backend's <c>UserOut</c> shape.</summary>
public sealed class UserOut
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsAdmin { get; set; }
    public bool Active { get; set; }

    public static UserOut From(UserEntity u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        IsAdmin = u.IsAdmin,
        Active = u.Active,
    };
}