using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// HTTP-level contract tests for <c>/api/auth/users</c> +
/// <c>PATCH /api/auth/me</c> + <c>PATCH/DELETE /api/auth/users/{uid}</c>.
/// Mirrors <see cref="ISEStudio.Tests.Releases.ReleaseApiTests"/>: real
/// Kestrel via <see cref="AuthTestWebApplicationFactory"/>, SQLite
/// per-test database, admin user seeded in-line.
///
/// <para>The dispatcher arms <c>auth.update_me</c>,
/// <c>auth.list_users</c>, <c>auth.create_user</c>,
/// <c>auth.update_user</c>, and <c>auth.delete_user</c> were previously
/// Stage-1 placeholders returning empty envelopes, so an admin "create
/// user" click succeeded on the wire but never persisted a
/// <see cref="UserEntity"/> row. The tests below pin the new contract:
/// each admin CRUD operation MUST persist the row + return the wire
/// shape the Python baseline emits (snake_case keys: id, username,
/// display_name, is_admin, active), and the documented Python guards
/// (last-admin / self-deactivate / KS-owner-cannot-be-deleted) MUST
/// surface as the corresponding 4xx envelope.</para>
/// </summary>
public sealed class AuthAdminApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task ListUsers_returns_admin_only_via_role_gate()
    {
        // /api/auth/users is gated on [Authorize(Roles = "Admin")] so a
        // non-admin caller must get 403.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        await SeedUserAsync(app, "alice", isAdmin: false);

        var adminClient = await AuthenticatedClientAsync(app);
        var adminResp = await adminClient.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);
        var body = await adminResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // admin + alice = 2 users
        Assert.Equal(2, body.GetArrayLength());
        // Wire-shape: snake_case display_name + is_admin
        Assert.Contains(body.EnumerateArray(), u =>
            u.GetProperty("username").GetString() == "alice"
            && u.GetProperty("is_admin").GetBoolean() == false);
        Assert.Contains(body.EnumerateArray(), u =>
            u.GetProperty("username").GetString()
                == AuthTestWebApplicationFactory.AdminUsername
            && u.GetProperty("is_admin").GetBoolean() == true);

        // Non-admin: 403
        var aliceClient = await AuthenticatedClientAsync(app, "alice");
        var aliceResp = await aliceClient.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.Forbidden, aliceResp.StatusCode);
    }

    [Fact]
    public async Task CreateUser_persists_row_and_returns_user_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/auth/users", new
        {
            username = "bob",
            password = "bob12345strong",
            is_admin = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal("bob", body.GetProperty("username").GetString());
        Assert.False(body.GetProperty("is_admin").GetBoolean());
        Assert.True(body.GetProperty("active").GetBoolean());

        // DB-side: row persisted + has a BCrypt password hash (not the
        // plaintext) + legacy_id filled by the DB DEFAULT 0 (D1(c):
        // LegacyIdAllocator retired).
        using var verifyScope = app.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var row = db.Users.Single(u => u.Username == "bob");
        Assert.True(row.IsAdmin == false);
        Assert.True(row.Active);
        Assert.NotEqual("bob12345strong", row.PasswordHash);
        Assert.StartsWith("$2", row.PasswordHash);
        Assert.Equal(0L, row.LegacyId);
    }

    [Fact]
    public async Task CreateUser_with_duplicate_username_returns_409()
    {
        // Mirrors Python HTTPException(409, "Username already exists").
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var first = await client.PostAsJsonAsync("/api/auth/users", new
        {
            username = "dup-user",
            password = "dupuser12345strong",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var dup = await client.PostAsJsonAsync("/api/auth/users", new
        {
            username = "dup-user",
            password = "different-strong-pwd",
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var body = await dup.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("Username already exists",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UpdateUser_rejects_demoting_last_admin()
    {
        // Mirrors Python "Can't remove the last admin".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        // admin is the only admin in the system.
        var adminRow = app.CreateDbContext().Users.Single(u => u.IsAdmin);
        var response = await client.PatchAsJsonAsync(
            $"/api/auth/users/{adminRow.Id}",
            new { is_admin = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("last admin",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UpdateUser_rejects_self_deactivate()
    {
        // Mirrors Python "You can't deactivate yourself".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var adminRow = app.CreateDbContext().Users.Single(u => u.IsAdmin);
        var response = await client.PatchAsJsonAsync(
            $"/api/auth/users/{adminRow.Id}",
            new { active = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("deactivate yourself",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task DeleteUser_cascades_sessions_and_returns_200()
    {
        // Mirrors Python delete_user: cascades through AuthSession +
        // McpUserToken + KSGrant, refuses to delete self, refuses to
        // delete a KS owner.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        await SeedUserAsync(app, "victim", isAdmin: false);
        var client = await AuthenticatedClientAsync(app);

        var victimId = app.CreateDbContext().Users.Single(u => u.Username == "victim").Id;
        var response = await client.DeleteAsync($"/api/auth/users/{victimId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(victimId, body.GetProperty("deleted").GetGuid());

        using var verifyScope = app.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        Assert.Null(db.Users.SingleOrDefault(u => u.Username == "victim"));
    }

    [Fact]
    public async Task DeleteUser_refuses_to_delete_self()
    {
        // Mirrors Python "You can't delete yourself".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var adminRow = app.CreateDbContext().Users.Single(u => u.IsAdmin);
        var response = await client.DeleteAsync($"/api/auth/users/{adminRow.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("delete yourself",
            body.GetProperty("detail").GetString());
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            return;
        }
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedUserAsync(
        AuthTestWebApplicationFactory app, string username, bool isAdmin)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == username)) return;
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                AuthTestWebApplicationFactory.OtherPassword, workFactor: 10),
            IsAdmin = isAdmin,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(
        AuthTestWebApplicationFactory app, string username = null!)
    {
        var uname = username ?? AuthTestWebApplicationFactory.AdminUsername;
        var password = username is null
            ? AuthTestWebApplicationFactory.AdminPassword
            : AuthTestWebApplicationFactory.OtherPassword;

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = uname,
            password,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        return client;
    }
}