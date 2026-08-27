using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// Contract tests for session authentication, login, logout, and the global
/// <c>{"detail": ...}</c> error envelope. These tests use
/// <see cref="AuthTestWebApplicationFactory"/> so the production pipeline
/// (DbContext, controllers, middleware) runs end-to-end.
/// </summary>
public sealed class AuthenticationContractTests
{
    [Fact]
    public async Task Login_sets_compatible_cookie_and_rejects_bad_password()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedUserAsync(app);
        var client = app.CreateClient();

        var bad = await client.PostAsJsonAsync("/api/auth/login",
            new { username = AuthTestWebApplicationFactory.AdminUsername, password = "bad" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("Incorrect username or password",
            (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var ok = await client.PostAsJsonAsync("/api/auth/login",
            new
            {
                username = AuthTestWebApplicationFactory.AdminUsername,
                password = AuthTestWebApplicationFactory.AdminPassword,
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        // Cookie attributes are case-insensitive per RFC 6265 / RFC 7230.
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_unknown_username_returns_generic_401()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedUserAsync(app);
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "nobody", password = "irrelevant" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Incorrect username or password",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Logout_clears_session_and_cookie()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (token, _) = await SeedUserWithSessionAsync(app);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"isestudio_session={token}");

        var response = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("isestudio_session=", setCookie);
        Assert.True(setCookie.Contains("expires=") || setCookie.Contains("max-age=0"),
            $"Expected logout cookie to clear the session; got: {setCookie}");
    }

    [Fact]
    public async Task Me_without_cookie_returns_enveloped_401()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedUserAsync(app);
        var client = app.CreateClient();

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Not authenticated",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Me_with_expired_session_returns_session_expired_detail()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (_, sessionId) = await SeedUserWithSessionAsync(app, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        _ = sessionId;
        var client = app.CreateClient();

        // Find the token directly from the seeded session row.
        var db = app.CreateDbContext();
        var token = db.AuthSessions.Single().Token;
        client.DefaultRequestHeaders.Add("Cookie", $"isestudio_session={token}");

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Session expired",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Me_with_session_for_inactive_user_returns_user_inactive_detail()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedUserAsync(app, makeAdminActive: false);
        var db = app.CreateDbContext();
        var user = db.Users.Single();
        db.AuthSessions.Add(new AuthSessionEntity
        {
            Token = "inactive-token",
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        db.SaveChanges();

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "isestudio_session=inactive-token");

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("User inactive",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Me_with_valid_session_returns_user()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedUserAsync(app);
        var db = app.CreateDbContext();
        var user = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var token = "valid-token";
        db.AuthSessions.Add(new AuthSessionEntity
        {
            Token = token,
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        db.SaveChanges();

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"isestudio_session={token}");
        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("username", out var username), $"Body was: {raw}");
        Assert.Equal(AuthTestWebApplicationFactory.AdminUsername, username.GetString());
        // Wire shape is snake_case (global JsonNamingPolicy.SnakeCaseLower
        // in Program.cs); the prior PascalCase assertion was a leftover
        // from before that policy went in.
        Assert.True(body.GetProperty("is_admin").GetBoolean());
        Assert.True(body.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Unknown_route_returns_envelope_not_problemdetails()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var client = app.CreateClient();
        var response = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.Equal("application/json", contentType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ProblemDetails", body);
        Assert.Contains("\"detail\"", body);
    }

    private static Task SeedUserAsync(AuthTestWebApplicationFactory app, bool makeAdminActive = true)
    {
        var db = app.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var existing = db.Users.SingleOrDefault(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        if (existing is null)
        {
            db.Users.Add(new UserEntity
            {
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
                IsAdmin = true,
                Active = makeAdminActive,
                CreatedAt = now,
            });
        }
        else
        {
            existing.Active = makeAdminActive;
        }
        db.SaveChanges();
        return Task.CompletedTask;
    }

    private static async Task<(Guid sessionId, Guid sessionId2)> SeedUserWithSessionAsync(
        AuthTestWebApplicationFactory app, DateTimeOffset? expiresAt = null)
    {
        await SeedUserAsync(app);
        var db = app.CreateDbContext();
        var user = db.Users.Single();
        var session = new AuthSessionEntity
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
        };
        db.AuthSessions.Add(session);
        db.SaveChanges();
        return (session.Id, session.Id);
    }
}
