using System.Security.Cryptography;
using System.Text;
using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class DocumentIngestionJobProcessorTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public DocumentIngestionJobProcessorTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Txt_blob_is_parsed_and_document_metadata_is_persisted()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var text = "甲😀乙\n\nSecond paragraph.";
        var document = await AddDocumentAsync(db, "parsed.txt");
        await PutBlobAsync(blobStore, db, document, text);

        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        var result = await scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None);

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("fallback:text", persisted.ParserBackend);
        Assert.Equal(text.EnumerateRunes().Count(), persisted.TextCharCount);
        Assert.Equal(result.Version.ChunkCount, persisted.ChunkCount);
        Assert.Equal(result.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == result.Version.Id));
    }

    [Fact]
    public async Task Missing_blob_fails_document_and_leaves_no_version_or_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await AddDocumentAsync(db, "missing.txt");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("failed", persisted.ParseStatus);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ParseError));
        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal("failed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    [Fact]
    public async Task Unsupported_extension_fails_without_version_or_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "unsupported.html");
        await PutBlobAsync(blobStore, db, document, "<html>no</html>");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        await Assert.ThrowsAsync<NotSupportedException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("failed", persisted.ParseStatus);
        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal("failed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    [Fact]
    public async Task Duplicate_delivery_reuses_the_same_version_and_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "duplicate.txt");
        await PutBlobAsync(blobStore, db, document, "same content");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        var processor = scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>();

        var first = await processor.ProcessAsync(job, CancellationToken.None);
        var second = await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(first.Version.Id, second.Version.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.Id == first.Version.Id));
        Assert.Equal(first.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Version.Id));
    }

    [Fact]
    public async Task Cross_knowledge_system_document_is_rejected_without_writes()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var otherSystem = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Stage 4 other system",
            GraphIri = "https://example.test/stage-4-other",
            BaseIri = "https://example.test/stage-4-other#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = otherSystem.Id,
            Sha256 = new string('d', 64),
            OriginalFilename = "cross-system.txt",
            Ext = "txt",
            StoragePath = "dd/cross-system",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(otherSystem);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal("failed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    private async Task<DocumentEntity> AddDocumentAsync(ISEStudioDbContext db, string filename)
    {
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Sha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
            OriginalFilename = filename,
            Ext = Path.GetExtension(filename).TrimStart('.'),
            StoragePath = string.Empty,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task PutBlobAsync(
        IBlobStore blobStore,
        ISEStudioDbContext db,
        DocumentEntity document,
        string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var written = await blobStore.PutAsync(content, CancellationToken.None);
        document.Sha256 = written.Sha256;
        document.StoragePath = written.LegacyStoragePath;
        document.SizeBytes = Encoding.UTF8.GetByteCount(text);
        await db.SaveChangesAsync();
    }
}