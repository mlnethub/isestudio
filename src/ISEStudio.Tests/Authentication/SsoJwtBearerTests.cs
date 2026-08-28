using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// JwtBearer end-to-end tests against the in-process fake Keycloak. Covers
/// the SSO happy path (sync + me round-trip), the azp gate, expiry, role
/// flattening + admin policy, deactivation on second pass, role/name
/// refresh on re-login, and the session-cookie co-existence regression
/// (cookie login still routes through SessionCookie even with JwtBearer
/// registered).
/// </summary>
public class SsoJwtBearerTests
{
    private static SsoTestWebApplicationFactory NewFactory() => new();

    private HttpClient Client(SsoTestWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ValidTokenCreatesUserAndMeReturnsUserOut()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-alice", azp: factory.Issuer.ClientId,
            preferredUsername: "alice", name: "Alice"));

        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        var body = await me.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal("alice", body.GetProperty("username").GetString());
        Assert.False(body.GetProperty("is_admin").GetBoolean());

        // Second request must not duplicate the row — sync is idempotent.
        var second = await client.GetAsync("/api/auth/me");
        second.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        Assert.Single(db.Users.Where(u => u.SubjectId == "sub-alice"));
    }

    [Fact]
    public async Task AzpMismatchIsRejected()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-evil", azp: "some-other-client"));

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-expired", azp: factory.Issuer.ClientId,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoleOpensAdminOnlyEndpoint()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-admin", azp: factory.Issuer.ClientId,
            preferredUsername: "boss", realmRoles: ["admin"]));

        var users = await client.GetAsync("/api/auth/users");

        users.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NonAdminRoleIsDeniedAdminOnlyEndpoint()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-viewer", azp: factory.Issuer.ClientId,
            preferredUsername: "viewer", realmRoles: ["viewer"]));

        var users = await client.GetAsync("/api/auth/users");

        // Authenticated (token valid) but missing the Admin role →
        // framework returns 403 Forbidden, not 401 Unauthorized.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, users.StatusCode);
    }

    [Fact]
    public async Task InactiveSyncedUserIsRejected()
    {
        await using var factory = NewFactory();
        var token = factory.Issuer.CreateToken(
            sub: "sub-off", azp: factory.Issuer.ClientId, preferredUsername: "off");
        var first = await Client(factory, token).GetAsync("/api/auth/me");
        first.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var user = await db.Users.SingleAsync(u => u.SubjectId == "sub-off");
            user.Active = false;
            await db.SaveChangesAsync();
        }

        var second = await Client(factory, token).GetAsync("/api/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task ReloginRefreshesRoleAndDisplayName()
    {
        await using var factory = NewFactory();
        var firstToken = factory.Issuer.CreateToken(
            sub: "sub-flip", azp: factory.Issuer.ClientId,
            preferredUsername: "flip", name: "Old Name");
        var first = await Client(factory, firstToken).GetAsync("/api/auth/me");
        first.EnsureSuccessStatusCode();

        var secondToken = factory.Issuer.CreateToken(
            sub: "sub-flip", azp: factory.Issuer.ClientId,
            preferredUsername: "flip", name: "New Name", realmRoles: ["admin"]);
        var second = await Client(factory, secondToken).GetAsync("/api/auth/me");
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.True(body.GetProperty("is_admin").GetBoolean());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var user = await db.Users.SingleAsync(u => u.SubjectId == "sub-flip");
        Assert.Equal("New Name", user.DisplayName);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task CookiePathStillWorksAlongsideJwtBearer()
    {
        // Co-existence regression: a cookie login (no Bearer header) on a
        // host with JwtBearer registered must still route through the
        // SessionCookie scheme — PolicyScheme only forwards Bearer to
        // JwtBearer; everything else falls through to the default.
        await using var factory = NewFactory();
        var db = factory.Services.CreateScope()
            .ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        db.Database.EnsureCreated();
        db.Users.Add(new UserEntity
        {
            Username = "local_admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin12345strong", workFactor: 10),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "local_admin",
            password = "admin12345strong",
        });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith("isestudio_session=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);

        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
    }
}