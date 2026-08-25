using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Settings;

/// <summary>
/// HTTP-level contract tests for <c>/api/settings</c> and
/// <c>/api/models</c>. Mirrors
/// <see cref="ISEStudio.Tests.Releases.ReleaseApiTests"/>: real Kestrel
/// via <see cref="AuthTestWebApplicationFactory"/>, SQLite per-test
/// database, admin user seeded in-line.
///
/// <para>The dispatcher arms <c>settings.*</c> were previously Stage-1
/// placeholders returning empty envelopes, so an admin "change default
/// model" click succeeded on the wire but never updated the
/// <see cref="SystemConfigEntity"/> singleton. The tests below pin the
/// new contract: a <c>PUT /api/settings</c> call MUST update the
/// singleton's <see cref="SystemConfigEntity.LlmProviderId"/> /
/// <see cref="SystemConfigEntity.EmbeddingProviderId"/> and the
/// returned wire shape MUST match the Python
/// <c>backend/app/api/settings_api.py:SettingsOut</c> contract.</para>
/// </summary>
public sealed class SettingsApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task Settings_get_returns_default_payload_when_no_config_row_exists()
    {
        // Fresh DB → no SystemConfig row yet; the service must materialise
        // the singleton with LegacyId == 1 (mirrors Python's
        // get_system_config side effect).
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var response = await client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        // Wire-shape matches Python SettingsOut: llm_provider_id /
        // embedding_provider_id (both null on a fresh install) +
        // available_models + temperature + system_language + extract_model.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("llm_provider_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("embedding_provider_id").ValueKind);
        Assert.NotEqual(JsonValueKind.Null,
            body.GetProperty("available_models").ValueKind);
        Assert.True(body.GetProperty("available_models").GetArrayLength() >= 1);

        // DB-side: singleton row materialised at LegacyId == 1.
        using var verifyScope = app.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var rows = db.SystemConfigs.ToList();
        Assert.Single(rows);
        Assert.Equal(SystemConfigEntity.SingletonLegacyId, rows[0].LegacyId);
    }

    [Fact]
    public async Task Settings_update_persists_llm_provider_pointer_and_returns_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var llmId = await SeedProviderAsync(app, "llm", "default-llm",
            baseUrl: "https://llm.example.com", model: "gpt-4o-mini");

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            llm_provider_id = llmId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(llmId, body.GetProperty("llm_provider_id").GetGuid());

        // DB-side: the SystemConfig singleton has the new LLM pointer.
        using (var verifyScope = app.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var cfg = db.SystemConfigs.Single();
            Assert.Equal(llmId, cfg.LlmProviderId);
            // UpdatedAt was bumped.
            Assert.True((DateTimeOffset.UtcNow - cfg.UpdatedAt).TotalMinutes < 1);
        }
    }

    [Fact]
    public async Task Settings_update_rejects_llm_pointer_to_embedding_entry()
    {
        // Python _require() rejects "Entry X is a embedding entry, not llm".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var embeddingId = await SeedProviderAsync(app, "embedding",
            "default-embedding", baseUrl: "https://emb.example.com",
            model: "text-embedding-3-small");

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            llm_provider_id = embeddingId,
        });

        // FastApiErrorMiddleware turns ValidationException into 400.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("embedding", body.GetProperty("detail").GetString());
        Assert.Contains("llm", body.GetProperty("detail").GetString());

        // DB-side: SystemConfig unchanged.
        using var verifyScope = app.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var cfg = db.SystemConfigs.SingleOrDefault();
        Assert.NotNull(cfg);
        Assert.Null(cfg!.LlmProviderId);
    }

    [Fact]
    public async Task Models_lists_choices_with_default_prepended()
    {
        // Mirrors Python available_models(): the .env-default extract model
        // appears first even when not in the operator's choice list.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);

        var response = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var models = body.GetProperty("models");
        Assert.True(models.GetArrayLength() >= 1);
        // The wire field is "default" (matches Python backend) and the
        // JSON projection uses @default to dodge the C# reserved keyword.
        var defaultModel = body.GetProperty("default").GetString();
        Assert.False(string.IsNullOrEmpty(defaultModel));
        // Default appears first in the list.
        Assert.Equal(defaultModel, models[0].GetString());
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

    private static async Task<Guid> SeedProviderAsync(
        AuthTestWebApplicationFactory app, string kind, string name,
        string baseUrl, string model)
    {
        var db = app.CreateDbContext();
        var entity = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = name,
            Kind = kind,
            BaseUrl = baseUrl,
            ApiKey = "test-api-key-1234567890",
            Model = model,
            ConcurrencyLimit = 8,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(
        AuthTestWebApplicationFactory app)
    {
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
        return client;
    }
}