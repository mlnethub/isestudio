using System.Text;
using System.Text.Json;
using ISEStudio.Documents;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class DurableExtractionWorkerTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public DurableExtractionWorkerTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Plain_text_job_is_dispatched_through_the_existing_document_processor()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var dispatcher = new ExtractionJobDispatcher(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), store);

        var document = await AddDocumentAsync(db, "durable-plain-text.txt");
        await PutBlobAsync(blobStore, db, document, "first paragraph\n\nsecond paragraph");
        var job = await CreateQueuedJobAsync(db, document.Id, DocumentIngestionJobProcessor.Kind, "stage-4-test");

        var claimed = await store.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed!.Id);

        await dispatcher.DispatchAsync(claimed, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("completed", persistedJob.Status);
        Assert.Equal("finalizing", persistedJob.Phase);
        Assert.Equal("stage-4-test", persistedJob.Model);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
    }

    [Fact]
    public async Task Unknown_kind_is_terminal_failure()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var dispatcher = new ExtractionJobDispatcher(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), store);

        var document = await AddDocumentAsync(db, "durable-unknown-kind.txt");
        await CreateQueuedJobAsync(db, document.Id, "unknown_kind", "stage-4-test");

        var claimed = await store.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(claimed!, CancellationToken.None));

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Kind == "unknown_kind");
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.False(string.IsNullOrWhiteSpace(persistedJob.Error));
    }

    [Fact]
    public async Task Malformed_payload_is_terminal_failure()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var dispatcher = new ExtractionJobDispatcher(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), store);

        var job = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = DocumentIngestionJobProcessor.Kind,
            Status = "pending",
            Model = string.Empty,
            Payload = JsonDocument.Parse("{\"knowledge_system_id\":\"" + _fixture.KnowledgeSystemId + "\"}"),
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
        };
        db.ExtractionJobs.Add(job);
        await db.SaveChangesAsync();

        var claimed = await store.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(claimed!, CancellationToken.None));

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.False(string.IsNullOrWhiteSpace(persistedJob.Error));
    }

    [Fact]
    public async Task Worker_stops_cleanly_when_cancelled_before_the_next_poll()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await worker.RunAsync(cancellation.Token);
    }

    private async Task<ExtractionJobEntity> CreateQueuedJobAsync(
        ISEStudioDbContext db,
        Guid documentId,
        string kind,
        string? model)
    {
        var payload = JsonSerializer.SerializeToDocument(new
        {
            knowledge_system_id = _fixture.KnowledgeSystemId,
            document_id = documentId,
            model,
        });

        var job = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = kind,
            Status = "pending",
            Model = model ?? string.Empty,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
        };
        db.ExtractionJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<DocumentEntity> AddDocumentAsync(ISEStudioDbContext db, string filename)
    {
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
            OriginalFilename = filename,
            Ext = Path.GetExtension(filename).TrimStart('.'),
            StoragePath = string.Empty,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task PutBlobAsync(IBlobStore blobStore, ISEStudioDbContext db, DocumentEntity document, string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var written = await blobStore.PutAsync(content, CancellationToken.None);
        document.Sha256 = written.Sha256;
        document.StoragePath = written.LegacyStoragePath;
        document.SizeBytes = Encoding.UTF8.GetByteCount(text);
        await db.SaveChangesAsync();
    }

    private sealed class TestDurableExtractionWorker : DurableExtractionWorker
    {
        public TestDurableExtractionWorker(
            IServiceScopeFactory scopeFactory,
            ExtractionJobStore store,
            TimeProvider clock)
            : base(scopeFactory, store, clock)
        {
        }

        public Task RunAsync(CancellationToken cancellationToken) => base.ExecuteAsync(cancellationToken);
    }
}