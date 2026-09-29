using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Tests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Tests.Sources;

public sealed class SourceApiTests
{
    [Fact]
    public async Task Source_kind_is_persisted_using_registered_canonical_name()
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "source-kind-casing");

        var response = await admin.PostAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
            new { kind = "FOLDER", name = "Uppercase request" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("folder", created.GetProperty("kind").GetString());
        using var db = app.CreateDbContext();
        Assert.Equal("folder", await db.Sources.Where(source => source.Id == created.GetProperty("id").GetGuid())
            .Select(source => source.Kind).SingleAsync());
    }

    [Fact]
    public async Task Source_create_rolls_back_when_audit_write_fails()
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "source-audit-atomicity");
        using (var db = app.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_source_create_audit BEFORE INSERT ON auditevent " +
                "WHEN NEW.action = 'source.create' BEGIN SELECT RAISE(ABORT, 'audit disabled'); END;");
        }

        var response = await admin.PostAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
            new { kind = "folder", name = "Must roll back" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var verifyDb = app.CreateDbContext();
        Assert.False(await verifyDb.Sources.AnyAsync(source =>
            source.KnowledgeSystemId == knowledgeSystemId && source.Name == "Must roll back"));
    }

    [Fact]
    public async Task Source_names_are_unique_within_each_knowledge_system()
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "source-name-unique");
        using var db = app.CreateDbContext();
        db.Sources.AddRange(
            new SourceEntity
            {
                KnowledgeSystemId = knowledgeSystemId,
                Kind = "folder",
                Name = "Concurrent duplicate",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new SourceEntity
            {
                KnowledgeSystemId = knowledgeSystemId,
                Kind = "folder",
                Name = "Concurrent duplicate",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Source_tokens_are_editor_only_sealed_and_never_in_normal_projections()
    {
        var testKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        using var app = new AuthTestWebApplicationFactory(passwordOverride: null, sourceEncryptionKey: testKey);
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "source-token");

        var viewerUsername = $"token-viewer-{Guid.NewGuid():N}";
        var editorUsername = $"token-editor-{Guid.NewGuid():N}";
        await app.SeedUserAsync(viewerUsername);
        await app.SeedUserAsync(editorUsername);
        Guid viewerId;
        Guid editorId;
        var source = new SourceEntity
        {
            KnowledgeSystemId = knowledgeSystemId,
            Kind = "api",
            Name = "Push endpoint",
            Config = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var db = app.CreateDbContext())
        {
            viewerId = await db.Users.Where(user => user.Username == viewerUsername)
                .Select(user => user.Id).SingleAsync();
            editorId = await db.Users.Where(user => user.Username == editorUsername)
                .Select(user => user.Id).SingleAsync();
            db.KSGrants.AddRange(
                new KSGrantEntity
                {
                    KnowledgeSystemId = knowledgeSystemId, UserId = viewerId,
                    Role = "viewer", CreatedAt = DateTimeOffset.UtcNow,
                },
                new KSGrantEntity
                {
                    KnowledgeSystemId = knowledgeSystemId, UserId = editorId,
                    Role = "editor", CreatedAt = DateTimeOffset.UtcNow,
                });
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }

        var viewer = app.CreateClient();
        await app.AuthenticateAsAsync(viewer, viewerUsername);
        var editor = app.CreateClient();
        await app.AuthenticateAsAsync(editor, editorUsername);
        var tokenBase = $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{source.Id}/token";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"{tokenBase}/rotate", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync($"{tokenBase}/reveal", content: null)).StatusCode);

        var rotate = await editor.PostAsync($"{tokenBase}/rotate", content: null);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        Assert.True(rotate.Headers.CacheControl?.NoStore);
        var rotatedToken = (await rotate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rotatedToken));

        var list = await editor.GetAsync($"/api/knowledge/{knowledgeSystemId}/ingestion-sources");
        var listJson = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetRawText();
        Assert.DoesNotContain("ingest_token", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IngestTokenCiphertext", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rotatedToken!, listJson, StringComparison.Ordinal);

        var detail = await editor.GetAsync($"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{source.Id}");
        var detailJson = (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetRawText();
        Assert.DoesNotContain("ingest_token", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rotatedToken!, detailJson, StringComparison.Ordinal);

        var reveal = await editor.PostAsync($"{tokenBase}/reveal", content: null);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        Assert.True(reveal.Headers.CacheControl?.NoStore);
        Assert.Equal(rotatedToken,
            (await reveal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());

        var rotateAgain = await editor.PostAsync($"{tokenBase}/rotate", content: null);
        Assert.Equal(HttpStatusCode.OK, rotateAgain.StatusCode);
        var nextToken = (await rotateAgain.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.NotEqual(rotatedToken, nextToken);

        using var verifyDb = app.CreateDbContext();
        var persistedCiphertext = await verifyDb.Sources.Where(item => item.Id == source.Id)
            .Select(item => item.IngestTokenCiphertext).SingleAsync();
        var folderSourceId = await verifyDb.Sources
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId && item.Kind == "folder")
            .Select(item => item.Id).SingleAsync();
        Assert.StartsWith("v1:", persistedCiphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedToken!, persistedCiphertext, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict,
            (await editor.PostAsync(
                $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{folderSourceId}/token/rotate",
                content: null)).StatusCode);

        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SourceService>();
        Assert.True(await service.VerifyAsync(knowledgeSystemId, source.Id, nextToken, CancellationToken.None));
        Assert.False(await service.VerifyAsync(knowledgeSystemId, source.Id, rotatedToken, CancellationToken.None));
        Assert.False(await service.VerifyAsync(knowledgeSystemId, source.Id, "incorrect", CancellationToken.None));
        Assert.False(await service.VerifyAsync(knowledgeSystemId, folderSourceId, nextToken, CancellationToken.None));
        Assert.False(await service.VerifyAsync(Guid.NewGuid(), source.Id, nextToken, CancellationToken.None));

        var tokenAuditDetails = await verifyDb.AuditEvents.AsNoTracking()
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId
                && item.Action.StartsWith("source.token."))
            .Select(item => item.Detail)
            .ToListAsync();
        Assert.Equal(3, tokenAuditDetails.Count);
        foreach (var auditDetail in tokenAuditDetails)
        {
            var json = auditDetail?.RootElement.GetRawText() ?? string.Empty;
            Assert.DoesNotContain(rotatedToken!, json, StringComparison.Ordinal);
            Assert.DoesNotContain(nextToken!, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Token_operations_fail_closed_without_a_key_while_folder_crud_works()
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "source-token-no-key");

        var folderCreate = await admin.PostAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources", new { kind = "folder", name = "Normal" });
        Assert.Equal(HttpStatusCode.Created, folderCreate.StatusCode);

        var source = new SourceEntity
        {
            KnowledgeSystemId = knowledgeSystemId,
            Kind = "api",
            Name = "Push endpoint",
            Config = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var db = app.CreateDbContext())
        {
            db.Sources.Add(source);
            await db.SaveChangesAsync();
        }

        var tokenBase = $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{source.Id}/token";
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await admin.PostAsync($"{tokenBase}/rotate", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await admin.PostAsync($"{tokenBase}/reveal", content: null)).StatusCode);
    }

    [Fact]
    public void ValidateConfiguration_rejects_invalid_nonempty_source_encryption_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceSecretProtector.ConfigurationKey] = "not-base64",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => SourceSecretProtector.ValidateConfiguration(configuration));
    }

    [Fact]
    public async Task Source_management_is_scoped_and_role_gated()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var knowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "sources-main");
        var otherKnowledgeSystemId = await CreateKnowledgeSystemAsync(admin, "sources-other");

        var viewerUsername = $"source-viewer-{Guid.NewGuid():N}";
        var editorUsername = $"source-editor-{Guid.NewGuid():N}";
        await app.SeedUserAsync(viewerUsername);
        await app.SeedUserAsync(editorUsername);
        Guid viewerId;
        Guid editorId;
        using (var db = app.CreateDbContext())
        {
            viewerId = await db.Users.Where(user => user.Username == viewerUsername)
                .Select(user => user.Id).SingleAsync();
            editorId = await db.Users.Where(user => user.Username == editorUsername)
                .Select(user => user.Id).SingleAsync();
            db.KSGrants.AddRange(
                new KSGrantEntity
                {
                    KnowledgeSystemId = knowledgeSystemId, UserId = viewerId,
                    Role = "viewer", CreatedAt = DateTimeOffset.UtcNow,
                },
                new KSGrantEntity
                {
                    KnowledgeSystemId = knowledgeSystemId, UserId = editorId,
                    Role = "editor", CreatedAt = DateTimeOffset.UtcNow,
                },
                new KSGrantEntity
                {
                    KnowledgeSystemId = otherKnowledgeSystemId, UserId = editorId,
                    Role = "editor", CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        var viewer = app.CreateClient();
        await app.AuthenticateAsAsync(viewer, viewerUsername);
        var editor = app.CreateClient();
        await app.AuthenticateAsAsync(editor, editorUsername);

        var listResponse = await viewer.GetAsync($"/api/knowledge/{knowledgeSystemId}/ingestion-sources");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var sourceList = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var defaultSource = Assert.Single(sourceList.EnumerateArray());
        var defaultSourceId = defaultSource.GetProperty("id").GetGuid();
        Assert.Equal("folder", defaultSource.GetProperty("kind").GetString());
        Assert.DoesNotContain("ingest_token", defaultSource.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IngestTokenCiphertext", defaultSource.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var detailResponse = await viewer.GetAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{defaultSourceId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonDocument.Parse("{}").RootElement.GetRawText(), detail.GetProperty("config").GetRawText());

        var deniedCreate = await viewer.PostAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources", new { kind = "folder", name = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.PostAsJsonAsync(
                $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
                new { kind = "not_registered", name = "Unavailable" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.PostAsJsonAsync(
                $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
                new { kind = "folder", name = "Scheduled", sync_interval_minutes = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await editor.PostAsJsonAsync(
                $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
                new { kind = "folder", name = "Default" })).StatusCode);

        var createResponse = await editor.PostAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources",
            new { kind = "folder", name = "Manual B", icon = "folder-mark" });
        Assert.True(createResponse.StatusCode == HttpStatusCode.Created,
            $"Expected Created, received {(int)createResponse.StatusCode}: {await createResponse.Content.ReadAsStringAsync()}");
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var createdSourceId = created.GetProperty("id").GetGuid();
        var updateResponse = await editor.PatchAsJsonAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{createdSourceId}",
            new { kind = "folder", name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal("folder-mark", updated.GetProperty("icon").GetString());

        var kindsResponse = await viewer.GetAsync($"/api/knowledge/{knowledgeSystemId}/ingestion-sources/kinds");
        Assert.Equal(HttpStatusCode.OK, kindsResponse.StatusCode);
        var kinds = await kindsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var folderKind = Assert.Single(kinds.EnumerateArray());
        Assert.Equal("folder", folderKind.GetProperty("kind").GetString());
        Assert.False(folderKind.GetProperty("active_sync").GetBoolean());
        Assert.Empty(folderKind.GetProperty("config_fields").EnumerateArray());

        var graphSourcesResponse = await viewer.GetAsync($"/api/knowledge/{knowledgeSystemId}/sources");
        Assert.Equal(HttpStatusCode.OK, graphSourcesResponse.StatusCode);
        Assert.Equal(JsonValueKind.Array,
            (await graphSourcesResponse.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);

        var otherSourceListResponse = await editor.GetAsync(
            $"/api/knowledge/{otherKnowledgeSystemId}/ingestion-sources");
        Assert.Equal(HttpStatusCode.OK, otherSourceListResponse.StatusCode);
        Assert.Single((await otherSourceListResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.GetAsync(
                $"/api/knowledge/{otherKnowledgeSystemId}/ingestion-sources/{createdSourceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync(
                $"/api/knowledge/{otherKnowledgeSystemId}/ingestion-sources/{createdSourceId}",
                new { kind = "folder", name = "Cross KS" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.DeleteAsync(
                $"/api/knowledge/{otherKnowledgeSystemId}/ingestion-sources/{createdSourceId}")).StatusCode);

        Guid documentId;
        using (var db = app.CreateDbContext())
        {
            var document = new DocumentEntity
            {
                KnowledgeSystemId = knowledgeSystemId,
                SourceId = createdSourceId,
                Sha256 = new string('a', 64),
                OriginalFilename = "kept.txt",
                Folder = "/archive",
                Ext = "txt",
                SizeBytes = 1,
                StoragePath = "aa/aa/kept",
                UploadedAt = DateTimeOffset.UtcNow,
                ExternalKey = "source-key-1",
                MissingSince = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(document);
            await db.SaveChangesAsync();
            documentId = document.Id;
        }

        var deleteResponse = await editor.DeleteAsync(
            $"/api/knowledge/{knowledgeSystemId}/ingestion-sources/{createdSourceId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        using (var db = app.CreateDbContext())
        {
            var persisted = await db.Documents.SingleAsync(document => document.Id == documentId);
            Assert.Null(persisted.SourceId);
            Assert.Null(persisted.ExternalKey);
            Assert.Null(persisted.MissingSince);
            Assert.Equal("/archive", persisted.Folder);
        }
    }

    private static async Task<Guid> CreateKnowledgeSystemAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new { name, description = name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

}