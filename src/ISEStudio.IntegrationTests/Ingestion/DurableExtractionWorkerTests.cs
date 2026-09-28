using System.Text;
using System.Text.Json;
using ISEStudio.Documents;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();

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
    public async Task Parser_job_validates_blob_sha_and_persists_parser_metadata()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var document = await AddDocumentAsync(db, "durable-parser.html");
        await PutBlobAsync(blobStore, db, document, "<html><body>durable parser</body></html>");
        var job = await CreateQueuedJobAsync(db, document.Id, DocumentIngestionJobProcessor.Kind, "stage-5-test");
        job.Payload = JsonSerializer.SerializeToDocument(new
        {
            knowledge_system_id = _fixture.KnowledgeSystemId,
            document_id = document.Id,
            document_sha256 = document.Sha256,
        });
        await db.SaveChangesAsync();

        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        var persistedDocument = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("completed", persistedJob.Status);
        Assert.Equal("text/html", persistedDocument.Mime);
        Assert.Equal("document-parser/2", persistedDocument.ParserVersion);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
    }

    [Fact]
    public async Task Parser_job_accepts_historical_payload_without_document_sha256()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var document = await AddDocumentAsync(db, "historical-parser.txt");
        await PutBlobAsync(blobStore, db, document, "historical payload");
        var job = await CreateQueuedJobAsync(db, document.Id, DocumentIngestionJobProcessor.Kind, "stage-5-test", includeDocumentSha256: false);

        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("completed", persistedJob.Status);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
    }

    [Fact]
    public async Task Parser_job_with_stale_blob_sha_fails_without_creating_a_version()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var document = await AddDocumentAsync(db, "stale-sha.txt");
        var job = await CreateQueuedJobAsync(db, document.Id, DocumentIngestionJobProcessor.Kind, "stage-5-test");
        job.Payload = JsonSerializer.SerializeToDocument(new
        {
            knowledge_system_id = _fixture.KnowledgeSystemId,
            document_id = document.Id,
            document_sha256 = new string('0', 64),
        });
        await db.SaveChangesAsync();

        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.Contains("SHA-256", persistedJob.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
    }

    [Fact]
    public async Task Tbox_job_is_claimed_and_sent_to_the_tbox_handler_once()
    {
        var tracker = new HandlerTracker();
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler>(sp =>
                new RecordingExtractionJobHandler(
                    ExtractionWire.KindTBox,
                    tracker,
                    sp.GetRequiredService<ExtractionJobStore>()));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        var job = await CreateQueuedExtractionJobAsync(
            db,
            ExtractionWire.KindTBox,
            JsonSerializer.SerializeToDocument(new
            {
                knowledge_system_id = _fixture.KnowledgeSystemId,
                job_id = Guid.NewGuid(),
                source_version = Guid.NewGuid(),
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("completed", persistedJob.Status);
        Assert.Equal("finalizing", persistedJob.Phase);
        Assert.Equal(1, tracker.GetInvocationCount(ExtractionWire.KindTBox));
    }

    [Fact]
    public async Task Abox_job_is_claimed_and_sent_to_the_abox_handler_once()
    {
        var tracker = new HandlerTracker();
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler>(sp =>
                new RecordingExtractionJobHandler(
                    ExtractionWire.KindABox,
                    tracker,
                    sp.GetRequiredService<ExtractionJobStore>()));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        var job = await CreateQueuedExtractionJobAsync(
            db,
            ExtractionWire.KindABox,
            JsonSerializer.SerializeToDocument(new
            {
                knowledge_system_id = _fixture.KnowledgeSystemId,
                job_id = Guid.NewGuid(),
                source_version = Guid.NewGuid(),
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("completed", persistedJob.Status);
        Assert.Equal("finalizing", persistedJob.Phase);
        Assert.Equal(1, tracker.GetInvocationCount(ExtractionWire.KindABox));
    }

    [Fact]
    public async Task Duplicate_handler_registration_is_terminal_failure_without_invocation()
    {
        var tracker = new HandlerTracker();
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler>(sp =>
                new RecordingExtractionJobHandler(
                    ExtractionWire.KindTBox,
                    tracker,
                    sp.GetRequiredService<ExtractionJobStore>()));
            services.AddScoped<IExtractionJobHandler>(sp =>
                new RecordingExtractionJobHandler(
                    ExtractionWire.KindTBox,
                    tracker,
                    sp.GetRequiredService<ExtractionJobStore>()));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();

        var job = await CreateQueuedExtractionJobAsync(
            db,
            ExtractionWire.KindTBox,
            JsonSerializer.SerializeToDocument(new
            {
                knowledge_system_id = _fixture.KnowledgeSystemId,
                job_id = Guid.NewGuid(),
                source_version = Guid.NewGuid(),
            }));

        var claimed = await store.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        await dispatcher.DispatchAsync(claimed!, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.Contains("multiple", persistedJob.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tracker.GetInvocationCount(ExtractionWire.KindTBox));
    }

    [Fact]
    public async Task Unknown_kind_is_terminal_failure()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();

        var document = await AddDocumentAsync(db, "durable-unknown-kind.txt");
        await CreateQueuedJobAsync(db, document.Id, "unknown_kind", "stage-4-test");

        var claimed = await store.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        await dispatcher.DispatchAsync(claimed!, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Kind == "unknown_kind");
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.False(string.IsNullOrWhiteSpace(persistedJob.Error));
    }

    [Fact]
    public async Task Malformed_payload_is_terminal_failure()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();

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

        await dispatcher.DispatchAsync(claimed!, CancellationToken.None);

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedJob.Phase);
        Assert.False(string.IsNullOrWhiteSpace(persistedJob.Error));
    }

    [Fact]
    public async Task Completed_job_is_not_reclaimed_and_handler_is_invoked_once()
    {
        var tracker = new HandlerTracker();
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler>(sp =>
                new RecordingExtractionJobHandler(
                    ExtractionWire.KindABox,
                    tracker,
                    sp.GetRequiredService<ExtractionJobStore>()));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        var job = await CreateQueuedExtractionJobAsync(
            db,
            ExtractionWire.KindABox,
            JsonSerializer.SerializeToDocument(new
            {
                knowledge_system_id = _fixture.KnowledgeSystemId,
                job_id = Guid.NewGuid(),
                source_version = Guid.NewGuid(),
            }));

        await worker.RunUntilTerminalAsync(job.Id, CancellationToken.None);

        var next = await store.ClaimNextAsync(CancellationToken.None);
        Assert.Null(next);
        Assert.Equal(1, tracker.GetInvocationCount(ExtractionWire.KindABox));
    }

    [Fact]
    public async Task Worker_stops_cleanly_when_cancelled_before_the_next_poll()
    {
        await using var services = await BuildServicesAsync(services =>
        {
            services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        });
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ExtractionJobStore>();
        var worker = new TestDurableExtractionWorker(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = DurableExtractionWorkerOptions.MinPollInterval,
            }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await worker.RunAsync(cancellation.Token);
    }

    private async Task<ServiceProvider> BuildServicesAsync(Action<IServiceCollection> configureHandlers)
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var connectionString = seedDb.Database.GetConnectionString()
            ?? throw new InvalidOperationException("PostgreSQL connection string was not available.");

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new TestDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddScoped<DocumentVersionStore>();
        services.AddSingleton<Chunker>(_ => new Chunker(size: 20, overlap: 0));
        services.AddScoped<PlainTextIngestionService>();
        services.AddScoped<PlainTextIngestionJobProcessor>();
        services.AddScoped<DocumentIngestionJobProcessor>();
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(
            Path.Combine(Path.GetTempPath(), "isestudio-stage5", Guid.NewGuid().ToString("N"))));
        services.AddScoped<ExtractionJobStore>(sp =>
            new ExtractionJobStore(
                sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(),
                TimeProvider.System,
                sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped<ExtractionJobDispatcher>();
        configureHandlers(services);
        return services.BuildServiceProvider();
    }

    private async Task<ExtractionJobEntity> CreateQueuedJobAsync(
        ISEStudioDbContext db,
        Guid documentId,
        string kind,
        string? model,
        bool includeDocumentSha256 = true)
    {
        var documentSha256 = includeDocumentSha256
            ? (await db.Documents.AsNoTracking().SingleAsync(item => item.Id == documentId)).Sha256
            : null;
        var payload = JsonSerializer.SerializeToDocument(new
        {
            knowledge_system_id = _fixture.KnowledgeSystemId,
            document_id = documentId,
            document_sha256 = documentSha256,
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

    private async Task<ExtractionJobEntity> CreateQueuedExtractionJobAsync(
        ISEStudioDbContext db,
        string kind,
        JsonDocument payload)
    {
        var job = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = kind,
            Status = "pending",
            Model = "stage-5-test",
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
        private readonly ExtractionJobStore _store;

        public TestDurableExtractionWorker(
            IServiceScopeFactory scopeFactory,
            ExtractionJobStore store,
            TimeProvider clock,
            IOptions<DurableExtractionWorkerOptions>? options = null)
            : base(scopeFactory, store, clock, NullLogger<DurableExtractionWorker>.Instance, options)
        {
            _store = store;
        }

        public Task RunAsync(CancellationToken cancellationToken) => base.ExecuteAsync(cancellationToken);

        public async Task RunUntilTerminalAsync(Guid jobId, CancellationToken cancellationToken)
        {
            using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runTask = base.ExecuteAsync(workerCancellation.Token);

            try
            {
                await _store.WaitAsync(jobId, cancellationToken);
            }
            finally
            {
                workerCancellation.Cancel();
                try
                {
                    await runTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;

        public TestDbContextFactory(DbContextOptions<ISEStudioDbContext> options)
        {
            _options = options;
        }

        public ISEStudioDbContext CreateDbContext() => new(_options);

        public Task<ISEStudioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class HandlerTracker
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Increment(string kind)
        {
            _counts[kind] = _counts.TryGetValue(kind, out var current) ? current + 1 : 1;
        }

        public int GetInvocationCount(string kind) => _counts.TryGetValue(kind, out var count) ? count : 0;
    }

    private sealed class RecordingExtractionJobHandler : IExtractionJobHandler
    {
        private readonly HandlerTracker _tracker;
        private readonly ExtractionJobStore _store;

        public RecordingExtractionJobHandler(
            string kind,
            HandlerTracker tracker,
            ExtractionJobStore store)
        {
            Kind = kind;
            _tracker = tracker;
            _store = store;
        }

        public string Kind { get; }

        public async Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
        {
            _tracker.Increment(Kind);
            await _store.MarkCompletedAsync(job.Id, cancellationToken);
        }
    }
}