using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Persistence;
using ISEStudio.Tests.Infrastructure;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// State-machine tests for <see cref="ExtractionOrchestrator"/>: job row
/// lifecycle, live progress, phase sequencing, prompt snapshots, terminology
/// metrics, and — the load-bearing one — RDF/SQL atomicity on merge failure.
///
/// <para>Everything runs against real collaborators (PostgreSQL RDF
/// fixture, <see cref="LocalCasBlobStore"/>, <see cref="DocumentParser"/>,
/// <see cref="Chunker"/>, SQLite-backed <see cref="ExtractionJobStore"/>);
/// only the LLM call is faked, so no external service is contacted.</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionStateTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/extraction-tests";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly PostgresRdfFixture _rdf = new();
    private readonly Guid _ksId;

    /// <summary>RDF store under test.</summary>
    private PostgresRdfGraphStore Store => _rdf.TBox;

    /// <summary>Graph coordinates for the seeded knowledge system.</summary>
    private KsContext Ks { get; } = new(GraphIri, BaseIri);

    /// <summary>Job-row reader/writer the orchestrator and the tests share.</summary>
    private ExtractionJobStore Jobs { get; }

    private ServiceProvider Services { get; }

    private readonly IBlobStore _blobs;

    /// <summary>The subject under test.</summary>
    private ExtractionOrchestrator Orchestrator { get; }

    /// <summary>Canned-reply chat client (named so call sites read as the plan specifies).</summary>
    private FakeChat FakeChat { get; } = new();

    /// <summary>Merge decorator that can be primed to fail.</summary>
    private FakeMerger Merger { get; }

    /// <summary>The request every test starts from.</summary>
    private ExtractionRequest Request { get; }

    public ExtractionStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-extraction-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _rdf.InitializeAsync().GetAwaiter().GetResult();
        _ksId = _rdf.KnowledgeSystemId;
        SeedTBox();

        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        _blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        var sha = PutDocument(_blobs);

        Jobs = new ExtractionJobStore(_contexts, TimeProvider.System);
        Merger = new FakeMerger(new ExtractionMerger(_rdf.Statements));

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(FakeChat);

        Services = BuildServices(() => Orchestrator!);
        Orchestrator = new ExtractionOrchestrator(
            Jobs,
            _blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(_rdf.Statements),
            new PromptSnapshotService(),
            Merger,
            _rdf.Statements,
            TimeProvider.System,
            scopes: Services.GetRequiredService<IServiceScopeFactory>(),
            duplicateJudge: new DuplicateJudge(
                new EmbeddingGeneratorFactory(Options.Create(new ISEStudioOptions()))));

        Request = new ExtractionRequest(
            KnowledgeSystemId: _ksId,
            BlobSha: sha,
            FileName: "extraction-fixture.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 2);
    }

    // ------------------------------------------------------------------
    // Required: RDF/SQL atomicity on merge failure
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Failed_merge_reverts_rdf_and_marks_job_failed()
    {
        FakeChat.EnqueueValidDelta();
        Merger.FailWith(new InvalidOperationException("merge failed"));
        var before = Store.DumpNQuads(Ks.TBoxGraph);
        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        await RunDurableWorkerUntilTerminalAsync(job.Id);
        Assert.Equal("failed", (await Jobs.GetAsync(job.Id))!.Status);
        Assert.Equal(before, Store.DumpNQuads(Ks.TBoxGraph));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Failed_merge_records_the_error_and_finish_time()
    {
        FakeChat.EnqueueValidDelta();
        Merger.FailWith(new InvalidOperationException("merge failed"));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        await RunDurableWorkerUntilTerminalAsync(job.Id);

        var finished = (await Jobs.GetAsync(job.Id))!;
        Assert.Equal("failed", finished.Status);
        Assert.Contains("merge failed", finished.Error);
        Assert.NotNull(finished.FinishedAt);
        // The seeded TBox is still exactly as it was: no orphan triples.
        Assert.Equal(1, ClassCount());
    }

    // ------------------------------------------------------------------
    // TBox happy path
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartTBoxAsync_persists_prompt_snapshot_when_complete()
    {
        FakeChat.EnqueueValidDeltas(8);

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        var diagnostic = (string?)null;
        if (finished.Status != "completed")
        {
            diagnostic = $"status={finished.Status} error={finished.Error} phase={finished.Phase} log={finished.Log} chatCalls={FakeChat.CallCount}";
        }
        Assert.True(finished.Status == "completed", diagnostic);
        Assert.NotNull(finished.PromptSnapshot);

        var prompts = finished.PromptSnapshot!.RootElement.GetProperty("prompts");
        var entry = prompts.GetProperty(TBoxExtractionService.PromptKey);
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("content").GetString()));
        Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);
        Assert.False(entry.GetProperty("overridden").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartTBoxAsync_merges_axioms_into_the_tbox_graph()
    {
        FakeChat.EnqueueValidDeltas(8);
        var before = ClassCount();

        // Diagnostic: prove the parser produces a delta with new classes.
        var sample = FakeChat.ValidTBoxDelta;
        var parsed = ExtractionDeltaParser.ParseTBox(sample);
        var parserDiag = $"parser: classes={parsed.Classes.Count} obj={parsed.ObjectProperties.Count} data={parsed.DataProperties.Count} ax={parsed.Axioms.Count}";

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        var diag = $"{parserDiag} status={finished.Status} error={finished.Error} phase={finished.Phase} " +
                   $"log={finished.Log} chatCalls={FakeChat.CallCount} " +
                   $"classes={ClassCount()} before={before} axiomsAdded={finished.AxiomsAdded}";
        Assert.True(finished.Status == "completed", diag);
        Assert.True(ClassCount() > before, diag);
        Assert.True(finished.AxiomsAdded > 0, diag);
        Assert.Empty(Store.Match(graphIri: Ks.ABoxGraph));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartTBoxAsync_updates_processed_chunks_progress()
    {
        FakeChat.EnqueueValidDeltas(8);
        // Park the chat client after the first chunk so the intermediate
        // progress value is observable without racing the background task.
        FakeChat.BlockAfter(1);

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);

        var finished = await RunDurableWorkerUntilTerminalAsync(
            job.Id,
            async () =>
            {
                var midway = await PollAsync(job.Id, j => j.ProcessedChunks >= 1);
                Assert.True(midway.TotalChunks > 1, "Fixture document must chunk into more than one span.");
                Assert.True(midway.ProcessedChunks < midway.TotalChunks, "Progress should still be partial while parked.");
                Assert.Equal("running", midway.Status);
                FakeChat.Release();
            });

        var diag = $"status={finished.Status} error={finished.Error} phase={finished.Phase} log={finished.Log}";
        Assert.True(finished.Status == "completed", diag);
        Assert.Equal(finished.TotalChunks, finished.ProcessedChunks);
        Assert.Equal(finished.TotalChunks, finished.ChunkIds.Count);
    }

    // ------------------------------------------------------------------
    // ABox
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartABoxAsync_writes_to_abox_graph()
    {
        for (var i = 0; i < 8; i++) FakeChat.EnqueueValidABoxDelta();
        var tboxBefore = Store.DumpNQuads(Ks.TBoxGraph);

        var job = await Orchestrator.StartABoxAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal("abox", finished.Kind);
        Assert.True(finished.IndividualsAdded > 0, "ABox extraction should create individuals.");

        // Instances land in the ABox graph — never in the schema graph.
        Assert.NotEmpty(_rdf.ABox.Match(graphIri: Ks.ABoxGraph));
        Assert.Equal(tboxBefore, Store.DumpNQuads(Ks.TBoxGraph));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartABoxAsync_records_unknown_classes()
    {
        // "Ghost" is not in the seeded TBox, so the mention is rejected and
        // counted rather than silently creating an untyped individual.
        FakeChat.Enqueue("""
            {"individuals": [{"label": "Casper", "class": "Ghost", "attributes": [], "relations": []}]}
            """);

        var job = await Orchestrator.StartABoxAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.NotNull(finished.UnknownClasses);
        Assert.Equal(1, finished.UnknownClasses!.RootElement.GetProperty("Ghost").GetInt32());
    }

    // ------------------------------------------------------------------
    // Combined
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task StartCombinedAsync_runs_tbox_then_abox_phases()
    {
        FakeChat.EnqueueValidDeltas(8);
        for (var i = 0; i < 8; i++) FakeChat.EnqueueValidABoxDelta();

        var job = await Orchestrator.StartCombinedAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        var diag = $"status={finished.Status} error={finished.Error} phase={finished.Phase} log={finished.Log}";
        Assert.True(finished.Status == "completed", diag);
        Assert.Equal("both", finished.Kind);

        // The job log is an append-only phase history, so the ordering can be
        // asserted deterministically rather than by racing the Phase column.
        var history = ExtractionJobLog.Phases(finished.Log);
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "abox", "terminology", "finalizing" },
            history);
        Assert.Equal("finalizing", finished.Phase);

        // Combined runs walk every chunk twice (once per layer).
        Assert.Equal(finished.TotalChunks, finished.ProcessedChunks);
        Assert.NotEmpty(_rdf.ABox.Match(graphIri: Ks.ABoxGraph));
    }

    // ------------------------------------------------------------------
    // Terminology
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Terminology_service_extracts_metrics()
    {
        FakeChat.EnqueueValidDeltas(8);

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await RunDurableWorkerUntilTerminalAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.True(finished.TermsAdded > 0, "Terminology sync should mint concepts for new classes.");
        Assert.Null(finished.TerminologyError);

        // Re-running against the same vocabulary maps rather than re-adds.
        // Python parity (P3-10): terms_mapped counts mappings the PASS
        // performed (fresh creates + adopted unmapped concepts), so an
        // idempotent rerun reports 0 — every entity already has its
        // mapped concept and the loop skips it.
        var second = new TerminologyService(_rdf.Statements).SyncAsync(Ks, CancellationToken.None);
        Assert.Equal(0, second.TermsAdded);
        Assert.Equal(0, second.TermsMapped);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Poll the job row until <paramref name="predicate"/> holds (or the job goes terminal).</summary>
    private async Task<ExtractionJobEntity> PollAsync(Guid id, Func<ExtractionJobEntity, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var job = await Jobs.GetAsync(id);
            if (job is not null && predicate(job)) return job;
            if (job is not null && job.Status is "completed" or "failed")
            {
                throw new InvalidOperationException(
                    $"Job reached '{job.Status}' before the predicate held (error: {job.Error ?? "none"}).");
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Timed out waiting for the job predicate.");
    }

    private int ClassCount() =>
        Store.Match(
            predicateIri: Vocabulary.RdfType,
            objectIri: Vocabulary.OwlClass,
            graphIri: Ks.TBoxGraph).Count;

    private ServiceProvider BuildServices(Func<ExtractionOrchestrator> orchestratorFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton<IRdfStatementRepository>(_rdf.Statements);
        services.AddSingleton(Jobs);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddScoped<EmbeddingGeneratorFactory>();
        services.AddScoped<DuplicateJudge>();
        services.AddScoped<ConflictService>();
        services.AddScoped<IConflictAgent, ConflictAgent>();
        services.AddScoped<IStructureAgent, StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<IKnowledgeStatsService, KnowledgeStatsService>();
        services.AddScoped<TerminologyAgent>();
        services.AddSingleton<IBlobStore>(_blobs);
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddSingleton(new Chunker(size: 200, overlap: 20));
        services.AddSingleton(new EndpointCapacityCoordinator());
        services.AddSingleton(new TBoxExtractionService(Options.Create(new ISEStudioOptions())));
        services.AddSingleton(new ABoxExtractionService(Options.Create(new ISEStudioOptions())));
        services.AddSingleton(new TerminologyService(_rdf.Statements));
        services.AddSingleton(new PromptSnapshotService());
        services.AddSingleton<IExtractionMerger>(Merger);
        services.AddSingleton<ExtractionOrchestrator>(_ => orchestratorFactory());
        services.AddDovetailPipelines();
        services.AddScoped<IExtractionJobHandler, TBoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, ABoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, CombinedExtractionJobHandler>();
        services.AddScoped<ExtractionJobDispatcher>();
        return services.BuildServiceProvider();
    }

    private async Task<ExtractionJobEntity> RunDurableWorkerUntilTerminalAsync(
        Guid jobId,
        Func<Task>? beforeWait = null)
    {
        var worker = new DurableExtractionWorker(
            Services.GetRequiredService<IServiceScopeFactory>(),
            Jobs,
            TimeProvider.System,
            NullLogger<DurableExtractionWorker>.Instance,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SupportedKinds = new[]
                {
                    ExtractionWire.KindTBox,
                    ExtractionWire.KindABox,
                    ExtractionWire.KindBoth,
                },
            }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var workerTask = worker.StartAsync(cancellation.Token);
        try
        {
            if (beforeWait is not null)
            {
                await beforeWait();
            }
            return await Jobs.WaitAsync(jobId, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await worker.StopAsync(CancellationToken.None);
            await workerTask;
        }
    }

    /// <summary>Seed a single <c>Person</c> class so the ABox mentions resolve.</summary>
    private void SeedTBox()
    {
        var statements = SchemaBuilder.BuildMutationStatements(
            BaseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: new[] { new PropertyMutation("age", "data", Domain: "Person", Range: "integer") },
                Axioms: Array.Empty<AxiomMutation>()),
            Ks.TBoxGraph);
        Store.AddStatements(Ks.TBoxGraph, statements);
    }

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = "openai",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Extraction fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>Write a fixture document that chunks into several spans.</summary>
    private static string PutDocument(IBlobStore blobs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            text.Append(
                $"Section {i}. A Person is a human being with an age. " +
                $"An Employee is a Person who works for an organisation. " +
                $"Alice is a Person aged forty two in section {i}.\n\n");
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        FakeChat.Release();
        Services.Dispose();
        _rdf.DisposeAsync().GetAwaiter().GetResult();
        _contexts.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Directory handles can linger briefly on Windows; a stale
            // temp directory must never fail a test run.
        }
    }
}
