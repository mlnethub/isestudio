using ISEStudio.Application.Foundation;
using ISEStudio.Authorization;
using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class KnowledgeSourceSchemaTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public KnowledgeSourceSchemaTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Concurrent_first_uploads_create_one_default_folder_source()
    {
        await using var services = _fixture.BuildServices();
        await using var setup = services.CreateAsyncScope();
        var db = setup.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var admin = new UserEntity
        {
            Username = $"source-{Guid.NewGuid():N}", IsAdmin = true, Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var ks = new KnowledgeSystemEntity
        {
            PublicId = Guid.NewGuid().ToString("N"), Name = "Concurrent source",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();

        async Task<Guid> UploadAsync(string content)
        {
            await using var scope = services.CreateAsyncScope();
            var service = new DocumentService(
                scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>(),
                TimeProvider.System, new KnowledgeSystemAccessService(),
                scope.ServiceProvider.GetRequiredService<IBlobStore>(), null!, null!, null!);
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var result = await service.UploadAsync(ks.Id, stream, "concurrent.txt", "text/plain",
                stream.Length, "/", new Actor(admin.Id.ToString()), CancellationToken.None);
            return result.SourceId!.Value;
        }

        var sourceIds = await Task.WhenAll(UploadAsync("first"), UploadAsync("second"));
        db.ChangeTracker.Clear();
        var sources = await db.Sources.Where(s => s.KnowledgeSystemId == ks.Id).ToListAsync();
        Assert.Single(sources);
        Assert.All(sourceIds, id => Assert.Equal(sources[0].Id, id));
        Assert.Equal(2, await db.Documents.CountAsync(d => d.KnowledgeSystemId == ks.Id));
    }

    [Fact]
    public async Task Source_can_be_removed_without_deleting_document_or_folder()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "folder",
            Name = "Manual",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Sources.Add(source);
        var document = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        document.SourceId = source.Id;
        await db.SaveChangesAsync();

        db.Sources.Remove(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persisted = await db.Documents.SingleAsync(d => d.Id == document.Id);
        Assert.Null(persisted.SourceId);
        Assert.Null(persisted.ExternalKey);
        Assert.Equal("/", persisted.Folder);
    }

    [Fact]
    public async Task Migration_backfills_only_documents_with_a_knowledge_system()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ISEStudioDbContext(options);
        await db.Database.MigrateAsync("20260908125753_ReleaseDraftPartialUnique");

        var ksId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO knowledgesystem (id, "PublicId", "Name", "GraphIri", "BaseIri", "CreatedAt", "UpdatedAt")
            VALUES ({ksId}, {ksId.ToString("N")}, {"K"}, {"https://example.test/graph"}, {"https://example.test/base#"}, {now}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO document (id, "KnowledgeSystemId", "Sha256", "OriginalFilename", "Folder", "Ext", "SizeBytes", "StoragePath", "UploadedAt")
                 VALUES ({documentId}, {ksId}, {new string('a', 64)}, {"owned.txt"}, {"/manual"}, {"txt"}, {1L}, {"owned"}, {now}),
                     ({orphanId}, NULL, {new string('b', 64)}, {"orphan.txt"}, {"/orphan"}, {"txt"}, {1L}, {"orphan"}, {now})
            """);

        await db.Database.MigrateAsync();
        var source = await db.Sources.SingleAsync(s => s.KnowledgeSystemId == ksId);
        Assert.Equal("folder", source.Kind);
        Assert.Equal("never", source.LastSyncStatus);
        Assert.Equal(0, source.LastSyncAdded);
        var owned = await db.Documents.SingleAsync(d => d.Id == documentId);
        var orphan = await db.Documents.SingleAsync(d => d.Id == orphanId);
        Assert.Equal(source.Id, owned.SourceId);
        Assert.Null(orphan.SourceId);
        Assert.Null(owned.ExternalKey);
        Assert.True(owned.IsManualUpload);
        Assert.True(orphan.IsManualUpload);
        Assert.Equal("/manual", owned.Folder);
        Assert.Equal(new string('b', 64), orphan.Sha256);
        var fileVersion = await db.DocumentFileVersions.SingleAsync(v => v.DocumentId == documentId);
        Assert.Equal(1, fileVersion.Version);
        Assert.Equal(owned.Sha256, fileVersion.Sha256);
        Assert.Equal(owned.SizeBytes, fileVersion.SizeBytes);
        Assert.Null(fileVersion.DocTime);
        Assert.Empty(await db.DocumentFileVersionSnapshots.ToListAsync());

        await db.Database.MigrateAsync();
        Assert.Equal(1, await db.Sources.CountAsync(s => s.KnowledgeSystemId == ksId));
    }

    [Fact]
    public async Task Null_external_keys_are_allowed_but_duplicate_source_keys_are_not()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "folder",
            Name = "Identity",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var first = new DocumentEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            SourceId = source.Id,
            Sha256 = new string('c', 64),
            OriginalFilename = "first.txt",
            Ext = "txt",
            UploadedAt = DateTimeOffset.UtcNow
        };
        var second = new DocumentEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            SourceId = source.Id,
            Sha256 = new string('d', 64),
            OriginalFilename = "second.txt",
            Ext = "txt",
            UploadedAt = DateTimeOffset.UtcNow
        };
        db.Sources.Add(source);
        db.Documents.AddRange(first, second);
        await db.SaveChangesAsync();
        first.ExternalKey = "same";
        await db.SaveChangesAsync();
        second.ExternalKey = "same";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Unknown_source_kind_is_rejected_by_database()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        db.Sources.Add(new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "watch_folder",
            Name = "Unsupported",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}