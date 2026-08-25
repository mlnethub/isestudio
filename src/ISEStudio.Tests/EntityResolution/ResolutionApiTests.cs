using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.EntityResolution;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ResolutionApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task GetQueue_returns_envelope_with_only_pending_items()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-queue");
        await SeedRowViaDbAsync(app, ksId, "apple", "pending");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/resolution/queue");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var first = body.GetProperty("items").EnumerateArray().First();
        Assert.Equal("apple", first.GetProperty("surface_form").GetString());
    }

    [Fact]
    public async Task GetDecisions_returns_only_resolved_items()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-decisions");
        await SeedRowViaDbAsync(app, ksId, "fig", "matched");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/resolution/decisions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var first = body.GetProperty("items").EnumerateArray().First();
        Assert.Equal("matched", first.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resolve_match_flips_status_and_returns_decision()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-resolve-match");
        var resId = await SeedRowViaDbAsync(app, ksId, "fig", "pending");

        var put = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/resolution/{resId}/resolve",
            new { action = "match", individual_iri = "http://example.com/individuals/fig-1" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("matched", body.GetProperty("status").GetString());
        Assert.Equal("http://example.com/individuals/fig-1", body.GetProperty("individual_iri").GetString());
    }

    [Fact]
    public async Task Resolve_match_without_individual_iri_returns_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-resolve-400");
        var resId = await SeedRowViaDbAsync(app, ksId, "fig", "pending");

        var put = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/resolution/{resId}/resolve",
            new { action = "match" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Revoke_removes_row_and_returns_revoked_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-revoke");
        var resId = await SeedRowViaDbAsync(app, ksId, "fig", "matched");

        var del = await client.DeleteAsync($"/api/knowledge/{ksId}/resolution/decisions/{resId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = await del.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(resId, body.GetProperty("revoked").GetInt64());
    }

    [Fact]
    public async Task EditReason_writes_reason_and_returns_updated_decision()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "api-resolution-reason");
        var resId = await SeedRowViaDbAsync(app, ksId, "fig", "matched");

        var patch = await client.PatchAsJsonAsync(
            $"/api/knowledge/{ksId}/resolution/decisions/{resId}",
            new { reason = "manual override" });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual override", body.GetProperty("reason").GetString());
    }

    // --- helpers (mirror ConflictApiTests seed pattern) ---

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            var passwordService = new ISEStudio.Authentication.PasswordService();
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"), Id = Guid.NewGuid(),
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = passwordService.Hash(AuthTestWebApplicationFactory.AdminPassword),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
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
            name = $"ks-{tag}", description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<long> SeedRowViaDbAsync(
        AuthTestWebApplicationFactory app, Guid ksId, string surface, string status)
    {
        var db = app.CreateDbContext();
        var row = new EntityResolutionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            SurfaceForm = surface,
            ClassIri = "http://example.com/Fruit",
            Status = status,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EntityResolutions.Add(row);
        await db.SaveChangesAsync();
        row.LegacyId = TestLegacyIds.Next("entityresolution");
        await db.SaveChangesAsync();
        return row.LegacyId;
    }
}