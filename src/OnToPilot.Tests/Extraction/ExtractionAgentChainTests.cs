using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OnToPilot.Conflicts;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Knowledge;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Storage;
using OnToPilot.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// Pipeline-level tests for the post-TBox agent chain (Python
/// extraction.py's conflicts → structure segment): the orchestrator
/// resolves the scoped conflict / structure services from a fresh DI scope
/// after the TBox layer commits, walks the job through the
/// <c>conflicts</c> / <c>structure</c> phases, and lets the agents act
/// despite the job's own running row.
///
/// <para>Fixture mirrors <see cref="ExtractionStateTests"/> (real Oxigraph
/// store, SQLite job rows, canned chat), plus a service provider built
/// over the same store / job store / chat factory so the agent chain's
/// scoped services resolve exactly like production DI.</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionAgentChainTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/agent-chain";
    private const string BaseIri = GraphIri + "/onto#";

    /// <summary>
    /// Canned TBox delta for every chunk: one connected class
    /// (Kennel ⊑ Person) so extraction itself leaves no class isolated —
    /// the fixture's seeded Centrifugal Pump stays the chain's only target.
    /// </summary>
    private const string TBoxDelta = """
        {
          "classes": [{"label": "Kennel", "comment": "Where dogs sleep"}],
          "object_properties": [],
          "data_properties": [],
          "subclass_of": [{"sub": "Kennel", "super": "Person"}],
          "disjoint_with": [],
          "equivalent_class": []
        }
        """;

    /// <summary>Conflict agent reply: merge the predspec family.</summary>
    private const string ConflictFinish = """
        {"action":"finish","resolution":"merge","confidence":0.92,"reason":"same relation, range noun baked in"}
        """;

    /// <summary>Conflict agent reply: leave it for a human (no recommendation).</summary>
    private const string ConflictSkip = """
        {"action":"finish","resolution":"skip","confidence":0.1,"reason":"unsure"}
        """;

    /// <summary>Structure agent reply: attach the isolated class under a new Pump class.</summary>
    private const string StructureProposal = """
        {"parent":"Pump","new":true,"confidence":0.95,"evidence":"A centrifugal pump is a kind of pump","reason":"explicit is-a in source"}
        """;

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly IBlobStore _blobs;

    private StoreWrapper Store { get; }

    private KsContext Ks { get; } = new(GraphIri, BaseIri);

    /// <summary>Job-row reader/writer the orchestrator and the tests share.</summary>
    private ExtractionJobStore Jobs { get; }

    /// <summary>Canned-reply chat client (same fixture pattern as ExtractionStateTests).</summary>
    private FakeChat FakeChat { get; } = new();

    /// <summary>DI scope factory backing the agent chain (the orchestrator resolves
    /// <see cref="ConflictService"/> / <see cref="ConflictAgent"/> / <see cref="StructureAgent"/>
    /// / <see cref="KnowledgeStatsService"/> from scopes created out of this provider).</summary>
    private ServiceProvider Services { get; }

    /// <summary>The subject under test, wired with <see cref="Services"/>.</summary>
    private ExtractionOrchestrator Orchestrator { get; }

    private ExtractionRequest Request { get; }

    public ExtractionAgentChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ontopilot-agent-chain-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        Store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        _blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        var sha = PutDocument(_blobs);

        Jobs = new ExtractionJobStore(_contexts, TimeProvider.System);

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(FakeChat);

        Services = BuildServices();
        Orchestrator = BuildOrchestrator(Services.GetRequiredService<IServiceScopeFactory>());

        Request = new ExtractionRequest(
            KnowledgeSystemId: _ksId,
            BlobSha: sha,
            FileName: "agent-chain.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 1);
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task TBox_run_appends_conflicts_and_structure_phases()
    {
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ConflictFinish);
        FakeChat.Enqueue(StructureProposal);

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));

        // The chain's trailing stats refresh (Python refresh_ks_stats at
        // extraction.py:344) re-synced the cached counters:
        // 4 seeded classes + Kennel (extraction) + Pump (structure agent).
        // The conflict agent auto-applied the 0.92-confidence merge (P0
        // decision), so the seeded "trains Dog" / "trains Cat" pair is now
        // ONE merged property.
        await using var db = _contexts.CreateDbContext();
        var ks = await db.KnowledgeSystems.SingleAsync(k => k.Id == _ksId);
        Assert.Equal(6, ks.ClassCount);
        Assert.Equal(1, ks.PropertyCount);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Conflict_agent_auto_applies_confident_decision_inside_the_pipeline()
    {
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ConflictFinish);
        FakeChat.Enqueue("{}"); // structure agent: no parent → left

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);

        // DetectAsync found the seeded "trains Dog" / "trains Cat" family;
        // the 0.92-confidence decision meets the auto-apply floor, so the
        // agent ran the merge and flipped the row to resolved (P0 product
        // decision) — no recommendation payload, one agent audit row.
        await using var db = _contexts.CreateDbContext();
        var row = await db.Conflicts.SingleAsync(c => c.KnowledgeSystemId == _ksId);
        Assert.Equal("predicate_specialization", row.Ctype);
        Assert.Equal("resolved", row.Status);
        Assert.Equal("merge", row.Resolution);
        Assert.NotNull(row.ResolvedAt);
        Assert.False(row.Payload!.RootElement.TryGetProperty("recommendation", out _));

        var audit = await db.AuditEvents.SingleAsync(e => e.KnowledgeSystemId == _ksId);
        Assert.Equal("conflict.resolve", audit.Action);
        Assert.Equal("conflict-agent", audit.ActorName);
        Assert.True(audit.Detail!.RootElement.GetProperty("agent").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Structure_agent_attaches_isolated_class_inside_the_pipeline()
    {
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ConflictSkip);
        FakeChat.Enqueue(StructureProposal);

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);

        var view = SchemaBuilder.BuildView(GraphIri, Store);
        var pump = view.Classes.Single(c => c.Label == "Pump");
        var centrifugal = view.Classes.Single(c => c.Label == "Centrifugal Pump");
        Assert.Contains(pump.Iri, centrifugal.Superclasses);

        await using var db = _contexts.CreateDbContext();
        var audit = await db.AuditEvents.SingleAsync(a => a.KnowledgeSystemId == _ksId);
        Assert.Equal("tbox.attach_isolated", audit.Action);
        Assert.Equal("structure-agent", audit.ActorName);
        Assert.Null(audit.ActorId);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Combined_run_places_the_agent_chain_between_the_layers()
    {
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ConflictSkip);
        FakeChat.Enqueue(StructureProposal);
        FakeChat.EnqueueValidABoxDelta();

        var job = await Orchestrator.StartCombinedAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        // Python's combined worker runs the chain between TBox and ABox so
        // predicate merges act on a still-empty ABox.
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "abox", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));
        Assert.NotEmpty(Store.Match(graph: new OntoNamedNode(Ks.ABoxGraph)));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Agent_chain_skipped_when_no_scope_factory_is_wired()
    {
        // Hand-built orchestrator (the pre-chain constructor shape): the
        // optional scope-factory seam leaves the chain off entirely.
        var bare = BuildOrchestrator(scopes: null);
        FakeChat.Enqueue(TBoxDelta);

        var job = await bare.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(
            new[] { "tbox", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));

        // No conflict detection ran, so the seeded predspec family never
        // produced a row.
        await using var db = _contexts.CreateDbContext();
        Assert.Empty(await db.Conflicts.Where(c => c.KnowledgeSystemId == _ksId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Agent_chain_exception_fails_the_job_but_keeps_the_tbox_layer()
    {
        using var failing = BuildServices(services =>
            services.AddScoped<ConflictService>(_ =>
                throw new InvalidOperationException("detect exploded")));
        var orchestrator = BuildOrchestrator(failing.GetRequiredService<IServiceScopeFactory>());
        FakeChat.Enqueue(TBoxDelta);

        var job = await orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("failed", finished.Status);
        Assert.Contains("detect exploded", finished.Error);
        // Python's agents run after cap.diff() already committed the TBox
        // capture — the extracted layer stays, only the job row fails.
        Assert.True(ClassCount() > 4);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Build the agent chain's service provider: the scoped services are
    /// registered over the same store / job store / chat factory the
    /// orchestrator uses, and the shared scoped <see cref="OnToPilotDbContext"/>
    /// lets <see cref="ConflictService.DetectAsync"/>'s rows be visible to
    /// <see cref="ConflictAgent.TriageAsync"/> in the same scope.
    /// </summary>
    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<OnToPilotDbContext>>(_contexts);
        services.AddScoped<OnToPilotDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<OnToPilotDbContext>>().CreateDbContext());
        services.AddSingleton(Store);
        services.AddSingleton(Jobs);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new OnToPilotOptions()));
        services.AddScoped<LegacyIdAllocator>();
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        services.AddScoped<StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<KnowledgeStatsService>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private ExtractionOrchestrator BuildOrchestrator(IServiceScopeFactory? scopes) =>
        new(
            Jobs,
            _blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new OnToPilotOptions())),
            new ABoxExtractionService(Options.Create(new OnToPilotOptions())),
            new TerminologyService(Store),
            new PromptSnapshotService(),
            new ExtractionMerger(Store),
            Store,
            TimeProvider.System,
            verify: null,
            scopes: scopes);

    private int ClassCount() =>
        Store.Match(
            predicateIri: Vocabulary.RdfType.Value,
            objectIri: Vocabulary.OwlClass.Value,
            graphIri: Ks.TBoxGraph).Count;

    /// <summary>
    /// Seed the TBox: Person / Dog / Cat plus the "trains Dog" / "trains Cat"
    /// property pair (a real predicate-specialization family the detector
    /// catches) and one isolated class (Centrifugal Pump) for the
    /// structure agent.
    /// </summary>
    private void SeedTBox()
    {
        var quads = SchemaBuilder.BuildMutation(
            BaseIri,
            new OntologyMutation(
                Classes: new[]
                {
                    new ClassMutation("Person", "Seeded fixture class"),
                    new ClassMutation("Centrifugal Pump"),
                    new ClassMutation("Dog"),
                    new ClassMutation("Cat"),
                },
                ObjectProperties: new[]
                {
                    new PropertyMutation("trains Dog", "object", Domain: "Person", Range: "Dog"),
                    new PropertyMutation("trains Cat", "object", Domain: "Person", Range: "Cat"),
                },
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Ks.TBoxGraph);
        Store.AddQuads(new OntoNamedNode(Ks.TBoxGraph), quads);
    }

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = "agent-chain-llm",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            // Keep the structure agent's Pass-1 fan-out deterministic.
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Agent chain fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Source chunk the structure agent reads its evidence excerpts from
        // (the orchestrator's own chunks come from the blob below; both
        // texts must surface the isolated class label).
        const string text = "A centrifugal pump is a kind of pump that moves fluid.";
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = _ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "source.txt",
            Folder = "/",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("chunk"),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>Write a fixture document short enough to chunk into a single span.</summary>
    private static string PutDocument(IBlobStore blobs)
    {
        var text = "A centrifugal pump is a kind of pump that moves fluid.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        FakeChat.Release();
        Services.Dispose();
        Store.Dispose();
        _contexts.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The Oxigraph handle can linger briefly on Windows; a stale
            // temp directory must never fail a test run.
        }
    }
}
