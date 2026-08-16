using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Authentication;

/// <summary>
/// Regression tests for the timing-safe login path. The login controller
/// must invoke BCrypt verification on every attempt — including when the
/// username doesn't exist — so an external observer can't measure the
/// username-existence side channel.
/// </summary>
public sealed class TimingSafeLoginTests
{
    [Fact]
    public async Task Login_unknown_username_still_invokes_password_verification()
    {
        var spy = new SpyPasswordService();
        await using var app = new AuthTestWebApplicationFactory(spy);
        // Trigger EnsureCreated so the users table exists for the SELECT.
        _ = app.CreateDbContext();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "nobody-exists", password = "any-password-1234" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Incorrect username or password",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.True(spy.VerifyCallCount >= 1,
            $"Expected at least one Verify call, but the spy recorded {spy.VerifyCallCount}.");
    }

    [Fact]
    public async Task Login_wrong_password_still_invokes_password_verification()
    {
        var spy = new SpyPasswordService();
        await using var app = new AuthTestWebApplicationFactory(spy);
        var db = app.CreateDbContext();
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = "$2a$12$" + new string('0', 53),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new
            {
                username = AuthTestWebApplicationFactory.AdminUsername,
                password = "definitely-wrong-password",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.VerifyCallCount >= 1,
            $"Expected at least one Verify call, but the spy recorded {spy.VerifyCallCount}.");
    }

    [Fact]
    public async Task Login_empty_payload_still_invokes_password_verification()
    {
        var spy = new SpyPasswordService();
        await using var app = new AuthTestWebApplicationFactory(spy);
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "", password = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.VerifyCallCount >= 1,
            $"Expected at least one Verify call, but the spy recorded {spy.VerifyCallCount}.");
    }

    /// <summary>
    /// Test double: counts every call to <see cref="Verify"/>. The default
    /// <c>Verify</c> returns <c>false</c> so the controller still rejects
    /// the login, which is exactly what the timing-safe test wants to
    /// observe.
    /// </summary>
    private sealed class SpyPasswordService : IPasswordService
    {
        private int _verifyCalls;

        public int VerifyCallCount => Volatile.Read(ref _verifyCalls);

        public string Hash(string password) => $"spy:{password}";

        public bool Verify(string password, string passwordHash)
        {
            Interlocked.Increment(ref _verifyCalls);
            return false;
        }

        public void Validate(string password, bool bootstrap = false)
        {
            // No-op: the controller does not call Validate on login.
        }
    }
}