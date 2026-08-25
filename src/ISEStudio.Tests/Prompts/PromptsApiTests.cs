using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Prompts;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Prompts;

[Collection(nameof(ExtractionTestCollection))]
public sealed class PromptsApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task List_returns_catalog_with_no_overrides()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-list-http");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total_overrides").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(PromptCatalog.All.Count, items.Count);
        Assert.All(items, i =>
        {
            Assert.False(i.GetProperty("is_overridden").GetBoolean());
            Assert.Equal(
                i.GetProperty("default_content").GetString(),
                i.GetProperty("effective_content").GetString());
        });
        // key/category/title/description/variables are populated for every catalog entry
        Assert.Contains(items, i => i.GetProperty("key").GetString() == "extraction.system");
        Assert.Contains(items, i => i.GetProperty("key").GetString() == "review.system");
        Assert.Contains(items, i => i.GetProperty("key").GetString() == "governance.system");
        Assert.Contains(items, i => i.GetProperty("key").GetString() == "validation.system");
    }

    [Fact]
    public async Task Update_then_list_reflects_override()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-update-http");

        var put = await client.PutAsJsonAsync(
            $"/api/knowledge/{ksId}/prompts/extraction.system",
            new { content = "NEW OVERRIDE BODY" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(putBody.GetProperty("is_overridden").GetBoolean());
        Assert.Equal("NEW OVERRIDE BODY", putBody.GetProperty("effective_content").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/knowledge/{ksId}/prompts");
        Assert.Equal(1, list.GetProperty("total_overrides").GetInt32());
        var ext = list.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "extraction.system");
        Assert.True(ext.GetProperty("is_overridden").GetBoolean());
        Assert.Equal("NEW OVERRIDE BODY", ext.GetProperty("effective_content").GetString());
        Assert.NotEqual(JsonValueKind.Null, ext.GetProperty("updated_at").ValueKind);
        Assert.Equal(
            AuthTestWebApplicationFactory.AdminDisplayName,
            ext.GetProperty("updated_by").GetString());
    }

    [Fact]
    public async Task Update_with_empty_content_returns_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-update-empty");
        var put = await client.PutAsJsonAsync(
            $"/api/knowledge/{ksId}/prompts/extraction.system",
            new { content = "   " });
        var bodyText = await put.Content.ReadAsStringAsync();
        Assert.True(
            (int)put.StatusCode == 400 || (int)put.StatusCode == 422,
            $"Expected 400/422, got {(int)put.StatusCode}: {bodyText}");
    }

    [Fact]
    public async Task Update_unknown_key_returns_404()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-update-unknown");
        var put = await client.PutAsJsonAsync(
            $"/api/knowledge/{ksId}/prompts/no.such.key",
            new { content = "X" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Restore_reverts_override_to_default_and_returns_prompt()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-restore-http");
        await client.PutAsJsonAsync(
            $"/api/knowledge/{ksId}/prompts/extraction.system",
            new { content = "OVERRIDE" });

        var del = await client.DeleteAsync(
            $"/api/knowledge/{ksId}/prompts/extraction.system");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = await del.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("is_overridden").GetBoolean());
        Assert.Equal(
            body.GetProperty("default_content").GetString(),
            body.GetProperty("effective_content").GetString());
    }

    [Fact]
    public async Task Restore_all_returns_204()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "prompts-restore-all-http");
        await client.PutAsJsonAsync(
            $"/api/knowledge/{ksId}/prompts/extraction.system",
            new { content = "X" });

        var res = await client.PostAsync(
            $"/api/knowledge/{ksId}/prompts/restore-all", content: null);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        // subsequent list shows zero overrides
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/knowledge/{ksId}/prompts");
        Assert.Equal(0, list.GetProperty("total_overrides").GetInt32());
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var client = app.CreateClient();
        var ksId = Guid.NewGuid(); // any value; auth fails before route resolution
        var response = await client.GetAsync($"/api/knowledge/{ksId}/prompts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- helpers (mirrors OntologyApiTests.SeedAdminAndClientAsync + CreateKsAsync) ---

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            var passwordService = new ISEStudio.Authentication.PasswordService();
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = passwordService.Hash(AuthTestWebApplicationFactory.AdminPassword),
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
        var adminId = db.Users
            .Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<Guid> CreateKsAsync(HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}