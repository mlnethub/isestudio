using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Authentication;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/tokens*</c> and
/// <c>/api/knowledge/{ks_id}/mcp/tokens*</c>. Mirrors
/// <see cref="OnToPilot.Tests.Releases.ReleaseApiTests"/>: real Kestrel
/// via <see cref="AuthTestWebApplicationFactory"/>, SQLite per-test
/// database, admin user seeded in-line.
///
/// <para>The dispatcher arms <c>tokens.*</c> and <c>mcp_tokens.*</c> were
/// previously Stage-1 placeholders returning empty envelopes, so a
/// frontend "create API token" click succeeded on the wire but never
/// minted a row. The tests below pin the new contract: a
/// <c>POST /tokens</c> call MUST result in a persisted
/// <see cref="KnowledgeApiTokenEntity"/> row plus a wire response that
/// matches the Python <c>TokenCreated</c> shape (id, name, scopes,
/// status, token_prefix, token); <c>POST /mcp/tokens</c> MUST mint a
/// <see cref="McpUserTokenEntity"/> row + surface the bearer exactly
/// once.</para>
/// </summary>
public sealed class TokenManagementApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // ---- tokens (knowledge-API) -------------------------------------------

    [Fact]
    public async Task Tokens_create_persists_row_and_returns_token_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "tokens-create");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/tokens",
            new
            {
                name = "agent-reader",
                scopes = new[] { "ontology:read", "vocabulary:read" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        // Wire-shape matches Python TokenCreated: id, name, scopes,
        // status, token_prefix, token, expires_at, can_reveal.
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal("agent-reader", body.GetProperty("name").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        var plaintext = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(plaintext));
        // Wire prefix matches the Python opk_<...>_<...> format.
        Assert.StartsWith("opk_", plaintext!);
        // First 16 chars of the plaintext appear in token_prefix so the UI
        // can display them without exposing the bearer.
        Assert.Equal(
            plaintext!.Substring(0, Math.Min(16, plaintext.Length)),
            body.GetProperty("token_prefix").GetString());

        // DB-side: a KnowledgeApiTokenEntity row exists with the same
        // SHA-256 hash the IKnowledgeApiTokenService produced.
        using (var verifyScope = app.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
            var rows = db.KnowledgeApiTokens
                .Where(t => t.KnowledgeSystemId == ks.Id)
                .ToList();
            Assert.Single(rows);
            var row = rows[0];
            Assert.Equal("agent-reader", row.Name);
            Assert.NotNull(row.TokenHash);
            // Hash must be 64 hex chars (SHA-256) and the persisted
            // plaintext MUST NOT match the row (only the digest is kept).
            Assert.Equal(64, row.TokenHash.Length);
            Assert.NotEqual(plaintext, row.TokenHash);
            // Scopes were normalized down to the canonical two.
            Assert.Equal(2, row.Scopes.Count);
            Assert.Contains("ontology:read", row.Scopes);
            Assert.Contains("vocabulary:read", row.Scopes);
        }
    }

    [Fact]
    public async Task Tokens_list_returns_active_and_revoked_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "tokens-list");

        // Create two tokens.
        var firstCreate = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/tokens",
            new { name = "token-a", scopes = new[] { "ontology:read" } });
        Assert.Equal(HttpStatusCode.OK, firstCreate.StatusCode);
        var firstId = (await firstCreate.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("id").GetGuid();

        var secondCreate = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/tokens",
            new { name = "token-b", scopes = new[] { "ontology:read" } });
        Assert.Equal(HttpStatusCode.OK, secondCreate.StatusCode);

        // Revoke the first one.
        var revoke = await client.DeleteAsync(
            $"/api/knowledge/{ks.Id}/tokens/{firstId}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // List — both rows present, status reflects state.
        var list = await client.GetAsync($"/api/knowledge/{ks.Id}/tokens");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, listBody.GetArrayLength());

        var rows = listBody.EnumerateArray()
            .Select(e => (name: e.GetProperty("name").GetString()!,
                          status: e.GetProperty("status").GetString()!))
            .ToList();
        Assert.Contains(rows, r => r.name == "token-a" && r.status == "revoked");
        Assert.Contains(rows, r => r.name == "token-b" && r.status == "active");
    }

    [Fact]
    public async Task Tokens_create_rejects_provenance_without_instances()
    {
        // Mirrors the Python 400 "Scope provenance:read requires instances:read".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "tokens-scopes");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/tokens",
            new
            {
                name = "agent-provenance-only",
                scopes = new[] { "provenance:read" },
            });

        // FastApiErrorMiddleware turns ValidationException into 400.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("provenance:read", body.GetProperty("detail").GetString());
        Assert.Contains("instances:read", body.GetProperty("detail").GetString());

        // DB-side: no token row should have been written.
        using var verifyScope = app.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        Assert.Empty(db.KnowledgeApiTokens.Where(t => t.KnowledgeSystemId == ks.Id));
    }

    // ---- mcp_tokens -------------------------------------------------------

    [Fact]
    public async Task McpTokens_create_persists_row_and_returns_plaintext_once()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "mcp-create");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/mcp/tokens",
            new
            {
                name = "agent-session",
                scopes = new[] { "mcp:read", "mcp:write" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var plaintext = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(plaintext));
        // Wire prefix matches the Python opm_<...>_<...> format.
        Assert.StartsWith("opm_", plaintext!);

        // DB-side: row persisted with SHA-256 hash only.
        using (var verifyScope = app.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
            var rows = db.McpUserTokens
                .Where(t => t.KnowledgeSystemId == ks.Id)
                .ToList();
            Assert.Single(rows);
            Assert.NotEqual(plaintext, rows[0].TokenHash);
            Assert.Equal(2, rows[0].Scopes.Count);
        }
    }

    [Fact]
    public async Task McpTokens_list_returns_only_calling_users_tokens()
    {
        // Two users, each gets a token — list should only return the
        // calling user's (mirrors Python list_mcp_tokens filter by
        // McpUserToken.user_id == user.id).
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var alice = await SeedUserAsync(app, "alice");
        var bob = await SeedUserAsync(app, "bob");
        var ks = await SeedKnowledgeSystemAsync(app, "mcp-list");

        // Alice mints a token via the dispatcher (as alice).
        var aliceClient = await AuthenticatedClientAsync(app, alice.Username);
        var aliceCreate = await aliceClient.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/mcp/tokens",
            new { name = "alice-token" });
        Assert.Equal(HttpStatusCode.OK, aliceCreate.StatusCode);

        // Bob mints a token via the dispatcher (as bob).
        var bobClient = await AuthenticatedClientAsync(app, bob.Username);
        var bobCreate = await bobClient.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/mcp/tokens",
            new { name = "bob-token" });
        Assert.Equal(HttpStatusCode.OK, bobCreate.StatusCode);

        // Alice's list contains only her token.
        var aliceList = await aliceClient.GetAsync(
            $"/api/knowledge/{ks.Id}/mcp/tokens");
        Assert.Equal(HttpStatusCode.OK, aliceList.StatusCode);
        var aliceBody = await aliceList.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, aliceBody.GetProperty("items").GetArrayLength());
        Assert.Equal("alice-token",
            aliceBody.GetProperty("items")[0].GetProperty("name").GetString());

        // Bob's list contains only his token.
        var bobList = await bobClient.GetAsync(
            $"/api/knowledge/{ks.Id}/mcp/tokens");
        var bobBody = await bobList.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, bobBody.GetProperty("items").GetArrayLength());
        Assert.Equal("bob-token",
            bobBody.GetProperty("items")[0].GetProperty("name").GetString());
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
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<UserEntity> SeedUserAsync(
        AuthTestWebApplicationFactory app, string username)
    {
        var db = app.CreateDbContext();
        var user = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                AuthTestWebApplicationFactory.OtherPassword, workFactor: 10),
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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

    private static async Task<KnowledgeSystemEntity> SeedKnowledgeSystemAsync(
        AuthTestWebApplicationFactory app, string tag)
    {
        var db = app.CreateDbContext();
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = $"token-tests-{tag}",
            Description = "Seed KS for token contract tests.",
            GraphIri = $"http://ontopilot.local/ks/{tag}",
            BaseIri = $"http://ontopilot.local/ks/{tag}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }
}