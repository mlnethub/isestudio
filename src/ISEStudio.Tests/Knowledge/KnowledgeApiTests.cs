using System.Net;
using System.Net.Http.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Knowledge;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge*</c> — KS CRUD + membership
/// + review stats. Mirrors <see cref="ISEStudio.Tests.Providers.ProvidersApiTests"/>:
/// <list type="bullet">
///   <item><description>Real Kestrel via <see cref="AuthTestWebApplicationFactory"/>.</description></item>
///   <item><description>SQLite, per-test database, admin user seeded in-line.</description></item>
///   <item><description>Raw <c>HttpClient</c> + <c>JsonElement</c> parsing so the
///   tests stay tolerant of harmless extra fields.</description></item>
/// </list>
/// </summary>
public sealed class KnowledgeApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task List_returns_empty_when_no_ks()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var response = await client.GetAsync("/api/knowledge");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Create_then_get_round_trips()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);

        var create = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = "Test KS",
            description = "smoke test",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("Test KS", created.GetProperty("name").GetString());
        Assert.Equal("smoke test", created.GetProperty("description").GetString());
        Assert.Equal("owner", created.GetProperty("my_role").GetString());
        // Phase 2: graph_iri / base_iri are stamped from the row's PublicId
        // (the wire-stable hex Guid), NOT any numeric surrogate. Stamp uses
        // the configured IriRoot (default http://goodcrew.local/ks) — see
        // ISEStudioOptions.IriRoot.
        var publicId = LookupKsPublicId(app, id);
        Assert.Equal($"http://goodcrew.local/ks/{publicId}", created.GetProperty("graph_iri").GetString());
        Assert.Equal($"http://goodcrew.local/ks/{publicId}/onto#", created.GetProperty("base_iri").GetString());

        var get = await client.GetAsync($"/api/knowledge/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(id, fetched.GetProperty("id").GetGuid());
        Assert.Equal("Test KS", fetched.GetProperty("name").GetString());
    }

    /// <summary>
    /// Phase 2 regression (C1): every newly-created KS must derive a
    /// distinct <c>graph_iri</c> and <c>base_iri</c>. The pre-fix code
    /// stamped both from a numeric surrogate that defaulted to 0 for
    /// every new row — so two new KSes silently collided on the same RDF
    /// graph and cross-contaminated TBox / ABox / Vocabulary data
    /// downstream.
    /// </summary>
    [Fact]
    public async Task CreateAsync_twice_yields_distinct_graph_and_base_iris()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);

        var firstId = await CreateKsAsync(client, "first");
        var secondId = await CreateKsAsync(client, "second");
        Assert.NotEqual(firstId, secondId);

        var first = await GetKsAsync(client, firstId);
        var second = await GetKsAsync(client, secondId);

        var firstGraph = first.GetProperty("graph_iri").GetString();
        var firstBase = first.GetProperty("base_iri").GetString();
        var secondGraph = second.GetProperty("graph_iri").GetString();
        var secondBase = second.GetProperty("base_iri").GetString();

        Assert.NotNull(firstGraph);
        Assert.NotNull(secondGraph);
        Assert.NotNull(firstBase);
        Assert.NotNull(secondBase);
        Assert.NotEqual(firstGraph, secondGraph);
        Assert.NotEqual(firstBase, secondBase);
        // Sanity: the IRIs should embed each row's distinct PublicId.
        var firstPublicId = LookupKsPublicId(app, firstId);
        var secondPublicId = LookupKsPublicId(app, secondId);
        Assert.NotEqual(firstPublicId, secondPublicId);
        Assert.Equal($"http://goodcrew.local/ks/{firstPublicId}", firstGraph);
        Assert.Equal($"http://goodcrew.local/ks/{firstPublicId}/onto#", firstBase);
        Assert.Equal($"http://goodcrew.local/ks/{secondPublicId}", secondGraph);
        Assert.Equal($"http://goodcrew.local/ks/{secondPublicId}/onto#", secondBase);
    }

    [Fact]
    public async Task Update_patches_fields()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);

        var ksId = await CreateKsAsync(client, "before");
        var patch = await client.PatchAsJsonAsync($"/api/knowledge/{ksId}", new
        {
            name = "after",
            description = "updated",
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = await patch.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("after", updated.GetProperty("name").GetString());
        Assert.Equal("updated", updated.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Delete_removes_ks_and_its_per_ks_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "doomed");

        // Seed a per-KS row that must be cascaded away.
        SeedConflict(app, ksId, status: "open", ctype: "cycle");

        var delete = await client.DeleteAsync($"/api/knowledge/{ksId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var db = app.CreateDbContext();
        Assert.False(db.KnowledgeSystems.Any(k => k.Id == ksId));
        Assert.False(db.Conflicts.Any(c => c.KnowledgeSystemId == ksId));
    }

    [Fact]
    public async Task ListMembers_includes_owner_and_grants()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "members");

        // Add a viewer grant to a seeded second user.
        var alice = await SeedUserAsync(app, "alice-viewer", isAdmin: false);
        await AddMemberAsync(client, ksId, alice.Username, "viewer");

        var members = await client.GetAsync($"/api/knowledge/{ksId}/members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        var body = await members.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetArrayLength());
        var roles = body.EnumerateArray()
            .Select(e => e.GetProperty("role").GetString())
            .OrderBy(r => r).ToArray();
        Assert.Equal(new[] { "owner", "viewer" }, roles);
    }

    [Fact]
    public async Task AddMember_then_RemoveMember()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "add-remove");
        var bob = await SeedUserAsync(app, "bob", isAdmin: false);

        await AddMemberAsync(client, ksId, bob.Username, "editor");

        var bobId = bob.Id;
        var remove = await client.DeleteAsync($"/api/knowledge/{ksId}/members/{bobId}");
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);

        var members = await client.GetAsync($"/api/knowledge/{ksId}/members");
        var body = await members.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Single(body.EnumerateArray());
    }

    [Fact]
    public async Task GrantableUsers_excludes_owner_and_members()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "candidates");
        var alice = await SeedUserAsync(app, "alice-grantable", isAdmin: false);
        var bob = await SeedUserAsync(app, "bob-grantable", isAdmin: false);
        await AddMemberAsync(client, ksId, bob.Username, "viewer");

        var grantable = await client.GetAsync($"/api/knowledge/{ksId}/members/candidates");
        Assert.Equal(HttpStatusCode.OK, grantable.StatusCode);
        var body = await grantable.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var usernames = body.EnumerateArray()
            .Select(e => e.GetProperty("username").GetString())
            .ToHashSet();
        Assert.Contains(alice.Username, usernames);
        Assert.DoesNotContain(bob.Username, usernames);
    }

    [Fact]
    public async Task ReviewCounts_returns_four_buckets_and_total()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "review-counts");

        SeedConflict(app, ksId, status: "open", ctype: "cycle");
        SeedConflict(app, ksId, status: "open", ctype: "duplicate");
        SeedConflict(app, ksId, status: "dismissed", ctype: "cycle");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/review/counts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("conflicts").GetInt32());
        Assert.Equal(0, body.GetProperty("resolution").GetInt32());
        Assert.Equal(0, body.GetProperty("terminology").GetInt32());
        Assert.Equal(0, body.GetProperty("validation").GetInt32());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            db.Users.Add(new UserEntity
            {
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
                IsAdmin = true,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var adminId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<UserEntity> SeedUserAsync(
        AuthTestWebApplicationFactory app, string username, bool isAdmin)
    {
        var db = app.CreateDbContext();
        var u = new UserEntity
        {
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(username + "-pwd", workFactor: 4),
            IsAdmin = isAdmin,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>POST a KS and return its wire primary-key <see cref="Guid"/>.</summary>
    private static async Task<Guid> CreateKsAsync(HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // The wire `id` is the KS primary-key Guid (the migration removed the
        // legacy integer from the DTO).
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>Resolve a KS PublicId (the wire-stable hex Guid) from its PK Guid.</summary>
    private static string LookupKsPublicId(AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksId)
            .Select(k => k.PublicId)
            .Single();
    }

    /// <summary>GET a KS and return its parsed JSON body.</summary>
    private static async Task<System.Text.Json.JsonElement> GetKsAsync(HttpClient client, Guid ksId)
    {
        var response = await client.GetAsync($"/api/knowledge/{ksId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    private static async Task AddMemberAsync(HttpClient client, Guid ksId, string username, string role)
    {
        var response = await client.PostAsJsonAsync($"/api/knowledge/{ksId}/members", new
        {
            username,
            role,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Guid SeedConflict(AuthTestWebApplicationFactory app, Guid ksGuid, string status, string ctype)
    {
        var db = app.CreateDbContext();
        var id = Guid.NewGuid();
        db.Conflicts.Add(new ConflictEntity
        {
            Id = id,
            KnowledgeSystemId = ksGuid,
            Signature = $"{ctype}|{Guid.NewGuid():N}",
            Ctype = ctype,
            Severity = "error",
            Status = status,
            Title = $"{ctype} (test seed)",
            Detail = "seed",
            Payload = System.Text.Json.JsonDocument.Parse("""{"entities":[],"resolutions":[]}"""),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }
}
