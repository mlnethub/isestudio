using System.Security.Claims;
using System.Text.Json;
using ISEStudio.Authentication;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Authentication;

public class SsoUserSyncServiceTests
{
    private readonly AuthTestWebApplicationFactory _factory = new();

    private static SsoUserSyncService NewService(ISEStudioDbContext db, string adminRole = "admin")
        => new(db, Options.Create(new SsoOptions { AdminRole = adminRole }), TimeProvider.System);

    private static ClaimsPrincipal Principal(
        string sub, string? preferredUsername = null, string? name = null,
        string[]? realmRoles = null)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (preferredUsername is not null) claims.Add(new("preferred_username", preferredUsername));
        if (name is not null) claims.Add(new("name", name));
        if (realmRoles is not null)
            claims.Add(new("realm_access", JsonSerializer.Serialize(
                new { roles = realmRoles }), "JSON"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task FirstSyncCreatesUserWithSubjectId()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(Principal("sub-1", preferredUsername: "alice", name: "Alice"), default);

        Assert.Equal("sub-1", user.SubjectId);
        Assert.Equal("alice", user.Username);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Empty(user.PasswordHash);
        Assert.False(user.IsAdmin);
        Assert.True(user.Active);
    }

    [Fact]
    public async Task SecondSyncRefreshesMutableFieldsWithoutDuplicating()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        await svc.SyncAsync(Principal("sub-2", preferredUsername: "bob", name: "Bob"), default);

        var refreshed = await svc.SyncAsync(
            Principal("sub-2", preferredUsername: "bob", name: "Bob Renamed", realmRoles: ["admin"]), default);

        Assert.Equal("bob", refreshed.Username);            // username 不可变
        Assert.Equal("Bob Renamed", refreshed.DisplayName);  // name 刷新
        Assert.True(refreshed.IsAdmin);                      // role 刷新
        Assert.Single(db.Users.Where(u => u.SubjectId == "sub-2"));
    }

    [Fact]
    public async Task UsernameCollisionAppendsSubSuffixIdempotently()
    {
        var db = _factory.CreateDbContext();
        db.Users.Add(new UserEntity
        {
            Username = "carol",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = NewService(db);

        var first = await svc.SyncAsync(Principal("sub-3", preferredUsername: "carol"), default);
        var second = await svc.SyncAsync(Principal("sub-3", preferredUsername: "carol"), default);

        Assert.Equal("carol~sub-3", first.Username);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task MissingPreferredUsernameFallsBackToSsoPrefix()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(Principal("abcdef0123456789"), default);

        Assert.Equal("sso_abcdef01", user.Username);
    }

    [Fact]
    public async Task AdminRoleMapsToIsAdmin()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(
            Principal("sub-4", preferredUsername: "dave", realmRoles: ["admin", "viewer"]), default);

        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task InactiveUserIsRejected()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        var created = await svc.SyncAsync(Principal("sub-5", preferredUsername: "eve"), default);
        created.Active = false;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SyncAsync(Principal("sub-5", preferredUsername: "eve"), default));
    }

    [Fact]
    public async Task MissingSubThrows()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SyncAsync(new ClaimsPrincipal(new ClaimsIdentity()), default));
    }

    [Fact]
    public async Task MalformedRealmAccessIsIgnored()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        var claims = new List<Claim> { new("sub", "sub-6"), new("realm_access", "not-json") };

        var user = await svc.SyncAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), default);

        Assert.False(user.IsAdmin);
    }

    [Fact]
    public void RealmRolesParsesJsonArray()
    {
        var principal = Principal("sub-7", realmRoles: ["admin", "editor"]);

        var roles = SsoClaimMapping.RealmRoles(principal).ToList();

        Assert.Equal(["admin", "editor"], roles);
    }
}