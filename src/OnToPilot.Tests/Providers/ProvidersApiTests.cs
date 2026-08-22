using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Providers;

/// <summary>
/// HTTP-level contract tests for <c>/api/providers*</c> — the wire shape
/// and authorization guarantees. Mirrors the auth contract tests:
/// <list type="bullet">
///   <item><description>Real Kestrel via <see cref="AuthTestWebApplicationFactory"/>.</description></item>
///   <item><description>SQLite, per-test database, admin user seeded in-line.</description></item>
///   <item><description>Raw <c>HttpClient</c> + <c>JsonElement</c> parsing; no
///   DTO type assertions so the tests stay tolerant of harmless extra fields.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>These tests are the regression gate for the CRUD replacement in
/// <see cref="OnToPilot.Providers.ProviderService"/>. The previous
/// dispatcher placeholders silently accepted POST / PATCH / DELETE and
/// returned success even though nothing was persisted; the very first test
/// asserts the list round-trips an inserted row so that future refactors
/// can't regress the wire contract without a red light here.</para>
/// </remarks>
public sealed class ProvidersApiTests
{
    private const string CookieHeader = "ontopilot_session";

    [Fact]
    public async Task List_returns_empty_when_no_providers()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var response = await client.GetAsync("/api/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Create_then_list_returns_row_with_masked_key()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        const string rawKey = "sk-or-v1-this-is-a-test-key-12345";
        var createResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "openrouter",
            kind = "llm",
            base_url = "https://openrouter.ai/api/v1",
            model = "deepseek/deepseek-chat",
            api_key = rawKey,
            concurrency_limit = 10,
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("openrouter", created.GetProperty("name").GetString());
        Assert.True(created.GetProperty("has_api_key").GetBoolean());
        var hint = created.GetProperty("api_key_hint").GetString() ?? string.Empty;
        Assert.Equal("••••" + rawKey[^4..], hint);

        var listResponse = await client.GetAsync("/api/providers");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetArrayLength());
        // Round-trip: the raw key MUST NOT appear anywhere in the list payload.
        var serialized = list.GetRawText();
        Assert.DoesNotContain(rawKey, serialized);
    }

    [Fact]
    public async Task Update_with_empty_api_key_preserves_existing()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        const string originalKey = "sk-or-v1-original-key-abc12345";
        var create = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "openrouter",
            kind = "llm",
            base_url = "https://openrouter.ai/api/v1",
            model = "deepseek/deepseek-chat",
            api_key = originalKey,
            concurrency_limit = 10,
        });
        var created = await createResponse(create);
        var id = created.GetProperty("id").GetString()!;

        // Patch with api_key = "" (the UI's "leave blank to keep" hint).
        var patch = await client.PatchAsJsonAsync($"/api/providers/{id}", new
        {
            concurrency_limit = 20,
            api_key = "",
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20, updated.GetProperty("concurrency_limit").GetInt32());
        // Hint unchanged: the stored key survived the patch.
        Assert.Equal("••••" + originalKey[^4..],
            updated.GetProperty("api_key_hint").GetString());
    }

    [Fact]
    public async Task Delete_blocked_when_provider_referenced_by_knowledge_system()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        const string rawKey = "sk-or-v1-protected-key-xyz67890";
        var create = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "openrouter",
            kind = "llm",
            base_url = "https://openrouter.ai/api/v1",
            model = "deepseek/deepseek-chat",
            api_key = rawKey,
            concurrency_limit = 10,
        });
        var created = await createResponse(create);
        var providerId = Guid.Parse(created.GetProperty("id").GetString()!);

        // Seed a knowledge system that points at this provider so the
        // FK reference guard must fire on the delete below.
        SeedReferencingKnowledgeSystem(app, providerId);

        var delete = await client.DeleteAsync($"/api/providers/{providerId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var body = await delete.Content.ReadFromJsonAsync<JsonElement>();
        var detail = body.GetProperty("detail").GetString() ?? string.Empty;
        Assert.Contains("referenced", detail, StringComparison.OrdinalIgnoreCase);

        // After the rejected delete, the row should still be listable.
        var list = await client.GetAsync("/api/providers");
        var arr = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, arr.GetArrayLength());
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        // Reuses the admin constants from AuthTestWebApplicationFactory so we
        // don't duplicate credential strings across test projects.
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

    private static async Task<HttpClient> AuthenticatedClientAsync(AuthTestWebApplicationFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        return client;
    }

    private static async Task<JsonElement> createResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void SeedReferencingKnowledgeSystem(AuthTestWebApplicationFactory app, Guid providerId)
    {
        var db = app.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Test KS",
            Description = "Created by ProvidersApiTests.Delete_blocked_when_provider_referenced_by_knowledge_system",
            GraphIri = "http://goodcrew.local/ks/test",
            BaseIri = "http://goodcrew.local/ks/test#",
            LlmProviderId = providerId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
}