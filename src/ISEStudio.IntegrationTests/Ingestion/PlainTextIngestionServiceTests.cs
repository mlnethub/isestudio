using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class PlainTextIngestionServiceTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public PlainTextIngestionServiceTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Plain_text_job_processor_completes_a_pending_job()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<PlainTextIngestionJobProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId
                && item.OriginalFilename == "fixture.txt");

        var job = new PlainTextIngestionJob(
            Guid.NewGuid(),
            _fixture.KnowledgeSystemId,
            document.Id,
            "job content",
            "stage-3-test");

        var result = await processor.ProcessAsync(job, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.Version.Id);
        Assert.Equal("completed", result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Plain_text_job_duplicate_delivery_reuses_version_and_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<PlainTextIngestionJobProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId
                && item.OriginalFilename == "fixture.txt");
        var job = new PlainTextIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "duplicate content", "stage-3-test");

        var first = await processor.ProcessAsync(job, CancellationToken.None);
        var second = await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(first.Version.Id, second.Version.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(
            item => item.DocumentId == document.Id && item.ContentSha256 == first.Version.ContentSha256));
        Assert.Equal(first.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Version.Id));
        Assert.Equal("completed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    [Fact]
    public async Task Plain_text_job_rejects_cross_system_document_without_writes()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var otherSystem = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Other system",
            GraphIri = "https://example.test/other",
            BaseIri = "https://example.test/other#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var otherDocument = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = otherSystem.Id,
            Sha256 = new string('b', 64),
            OriginalFilename = "other.txt",
            Ext = "txt",
            SizeBytes = 1,
            StoragePath = "bb/other",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(otherSystem);
        db.Documents.Add(otherDocument);
        await db.SaveChangesAsync();

        var processor = scope.ServiceProvider.GetRequiredService<PlainTextIngestionJobProcessor>();
        var job = new PlainTextIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, otherDocument.Id, "cross-system", "stage-3-test");
        var versionsBefore = await db.DocumentVersions.CountAsync(
            item => item.DocumentId == otherDocument.Id);
        var chunksBefore = await db.DocumentVersionChunks.CountAsync(
            item => db.DocumentVersions.Any(version =>
                version.Id == item.DocumentVersionId && version.DocumentId == otherDocument.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(job, CancellationToken.None));

        var persistedJob = await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Contains("does not belong", persistedJob.Error);
        Assert.Equal(versionsBefore, await db.DocumentVersions.CountAsync(
            item => item.DocumentId == otherDocument.Id));
        Assert.Equal(chunksBefore, await db.DocumentVersionChunks.CountAsync(
            item => db.DocumentVersions.Any(version =>
                version.Id == item.DocumentVersionId && version.DocumentId == otherDocument.Id)));
    }

    [Fact]
    public async Task Plain_text_job_failure_is_observable_and_retryable()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var missingDocumentId = Guid.NewGuid();
        var processor = scope.ServiceProvider.GetRequiredService<PlainTextIngestionJobProcessor>();
        var job = new PlainTextIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, missingDocumentId, "retry content", "stage-3-test");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(job, CancellationToken.None));
        var failed = await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));

        db.Documents.Add(new DocumentEntity
        {
            Id = missingDocumentId,
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Sha256 = new string('c', 64),
            OriginalFilename = "retry.txt",
            Ext = "txt",
            SizeBytes = 1,
            StoragePath = "cc/retry",
            UploadedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await processor.ProcessAsync(job, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal(result.Version.Id, await db.DocumentVersions
            .Where(item => item.DocumentId == missingDocumentId)
            .Select(item => item.Id)
            .SingleAsync());
    }

    [Fact]
    public async Task Plain_text_is_hashed_chunked_and_persisted_with_metadata()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId
                && item.OriginalFilename == "fixture.txt");
        var text = "First paragraph.\n\nSecond paragraph.";

        var result = await new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 20, overlap: 0))
            .IngestAsync(_fixture.KnowledgeSystemId, document.Id, text, CancellationToken.None);

        var expectedSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
        Assert.Equal(expectedSha, result.ContentSha256);
        Assert.Equal(2, result.ChunkCount);

        var chunks = await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == result.Id)
            .OrderBy(item => item.Idx)
            .ToListAsync();
        Assert.Collection(
            chunks,
            first =>
            {
                Assert.Equal(0, first.Idx);
                Assert.Equal("First paragraph.", first.Text);
                Assert.Equal(0, first.CharStart);
                Assert.Equal(16, first.CharEnd);
                Assert.Equal(6, first.TokenEstimate);
            },
            second =>
            {
                Assert.Equal(1, second.Idx);
                Assert.Equal("Second paragraph.", second.Text);
                Assert.Equal(18, second.CharStart);
                Assert.Equal(35, second.CharEnd);
                Assert.Equal(6, second.TokenEstimate);
            });
    }

    [Fact]
    public async Task Unicode_scalar_offsets_and_token_estimates_are_persisted()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId
                && item.OriginalFilename == "fixture.txt");
        var text = "甲😀乙😀丙😀丁";

        var result = await new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 3, overlap: 0))
            .IngestAsync(_fixture.KnowledgeSystemId, document.Id, text, CancellationToken.None);

        var chunks = await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == result.Id)
            .OrderBy(item => item.Idx)
            .ToListAsync();

        Assert.Collection(
            chunks,
            first =>
            {
                Assert.Equal("甲😀乙", first.Text);
                Assert.Equal(0, first.CharStart);
                Assert.Equal(3, first.CharEnd);
                Assert.Equal(3, first.TokenEstimate);
            },
            second =>
            {
                Assert.Equal("😀丙😀", second.Text);
                Assert.Equal(3, second.CharStart);
                Assert.Equal(6, second.CharEnd);
                Assert.Equal(3, second.TokenEstimate);
            },
            third =>
            {
                Assert.Equal("丁", third.Text);
                Assert.Equal(6, third.CharStart);
                Assert.Equal(7, third.CharEnd);
                Assert.Equal(1, third.TokenEstimate);
            });
    }

    [Fact]
    public async Task Repeating_text_is_idempotent_and_changed_text_creates_a_new_version()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId
                && item.OriginalFilename == "fixture.txt");
        var service = new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 20, overlap: 0));

        var first = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "same content", CancellationToken.None);
        var repeat = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "same content", CancellationToken.None);
        var changed = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "changed content", CancellationToken.None);

        Assert.Equal(first.Id, repeat.Id);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(
            item => item.Id == first.Id));
        Assert.Equal(1, await db.DocumentVersions.CountAsync(
            item => item.Id == changed.Id));
        Assert.Equal(1, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Id));
        Assert.Equal(1, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == changed.Id));
    }
}