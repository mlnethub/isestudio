using System.Text.Json;
using System.Net.Http.Json;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Tests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Tests.Sources;

public sealed class SourceConfigSecurityTests
{
    [Fact]
    public async Task Config_patch_seals_secret_fields_preserves_omitted_values_and_hides_secrets()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        using var app = new AuthTestWebApplicationFactory(passwordOverride: null, sourceEncryptionKey: key);
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin);
        using var db = app.CreateDbContext();
        var actor = await db.Users.SingleAsync(user => user.Username == AuthTestWebApplicationFactory.AdminUsername);
        var protector = app.Services.GetRequiredService<ISourceSecretProtector>();
        var registry = new SourceAdapterRegistry(new[]
        {
            new SourceKindDescriptor(SourceKind.Folder, false, Array.Empty<SourceConfigField>()),
            new SourceKindDescriptor(SourceKind.Api, false, new[]
            {
                new SourceConfigField("endpoint", "string", Required: true, Secret: false),
                new SourceConfigField("api_key", "string", Required: false, Secret: true),
            }),
        });
        var service = new SourceService(db, new KnowledgeSystemAccessService(), registry, protector, TimeProvider.System);

        const string firstSecret = "initial-api-secret";
        var create = await service.CreateAsync(
            knowledgeSystemId,
            new SourceUpsertRequest("api", "Test API", Config: ParseConfig(
                "{\"endpoint\":\"https://example.test/api\",\"api_key\":\"initial-api-secret\"}")),
            actor,
            CancellationToken.None);
        Assert.Equal(201, create.StatusCode);
        Assert.NotNull(create.Value);
        var sourceId = create.Value.Id;

        var persisted = await db.Sources.SingleAsync(source => source.Id == sourceId);
        using (var storedConfig = JsonDocument.Parse(persisted.Config))
        {
            Assert.Equal("https://example.test/api", storedConfig.RootElement.GetProperty("endpoint").GetString());
            var ciphertext = storedConfig.RootElement.GetProperty("api_key").GetString();
            Assert.StartsWith("v1:", ciphertext, StringComparison.Ordinal);
            Assert.DoesNotContain(firstSecret, ciphertext, StringComparison.Ordinal);
            Assert.Equal(firstSecret, protector.Open(
                ciphertext!, $"{knowledgeSystemId:D}:{sourceId:D}:api_key"));
        }

        var detail = await service.GetAsync(knowledgeSystemId, sourceId, actor, CancellationToken.None);
        Assert.Equal(200, detail.StatusCode);
        Assert.Equal("https://example.test/api", detail.Value!.Config.GetProperty("endpoint").GetString());
        Assert.False(detail.Value.Config.TryGetProperty("api_key", out _));

        const string replacementSecret = "replacement-api-secret";
        var update = await service.UpdateAsync(
            knowledgeSystemId,
            sourceId,
            new SourceUpsertRequest("api", "Test API", Config: ParseConfig(
                "{\"api_key\":\"replacement-api-secret\"}")),
            actor,
            CancellationToken.None);
        Assert.Equal(200, update.StatusCode);
        persisted = await db.Sources.SingleAsync(source => source.Id == sourceId);
        using (var storedConfig = JsonDocument.Parse(persisted.Config))
        {
            Assert.Equal("https://example.test/api", storedConfig.RootElement.GetProperty("endpoint").GetString());
            var ciphertext = storedConfig.RootElement.GetProperty("api_key").GetString();
            Assert.DoesNotContain(replacementSecret, ciphertext, StringComparison.Ordinal);
            Assert.Equal(replacementSecret, protector.Open(
                ciphertext!, $"{knowledgeSystemId:D}:{sourceId:D}:api_key"));
        }

        var clearSecret = await service.UpdateAsync(
            knowledgeSystemId,
            sourceId,
            new SourceUpsertRequest("api", "Test API", Config: ParseConfig("{\"api_key\":null}")),
            actor,
            CancellationToken.None);
        Assert.Equal(200, clearSecret.StatusCode);
        persisted = await db.Sources.SingleAsync(source => source.Id == sourceId);
        using (var storedConfig = JsonDocument.Parse(persisted.Config))
        {
            Assert.Equal("https://example.test/api", storedConfig.RootElement.GetProperty("endpoint").GetString());
            Assert.False(storedConfig.RootElement.TryGetProperty("api_key", out _));
        }

        var auditDetails = await db.AuditEvents.AsNoTracking()
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId)
            .Select(item => item.Detail)
            .ToListAsync();
        foreach (var auditDetail in auditDetails)
        {
            var json = auditDetail?.RootElement.GetRawText() ?? string.Empty;
            Assert.DoesNotContain(firstSecret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(replacementSecret, json, StringComparison.Ordinal);
        }
    }

    private static JsonElement? ParseConfig(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<Guid> CreateKnowledgeSystemAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = "source-config-security",
            description = "source-config-security",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}