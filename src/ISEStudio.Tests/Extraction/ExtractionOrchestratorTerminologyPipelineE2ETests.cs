using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
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
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// End-to-end test for the Dovetail terminology pipeline wired through
/// <see cref="ExtractionOrchestrator.RunTerminologyAsync"/>: the per-job
/// scope resolves <see cref="Dovetail.Terminology.TerminologyPipeline"/>
/// (the ctor seam stays null), the 5-segment DAG runs the deterministic
/// sync + the P3-1 proposal agent, and the job row records the folded
/// result. The fixture mirrors TerminologyAgentOrchestrationTests — which
/// pins the P1-4 fallback chain — except BuildServices additionally
/// registers AddDovetailPipelines + the terminology service + the
/// agent-chain interface forwarders (RunAgentChainAsync scope-resolves
/// AgentChainPipeline since Slice 3 R2, so the forwarders must exist for
/// the job to survive the agent chain on the DAG path).
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionOrchestratorTerminologyPipelineE2ETests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/term-dag";
    private const string BaseIri = GraphIri + "/onto#";

    private const string TBoxDelta = """
        {
          "classes": [
            {"label": "Pump", "comment": "A device that moves fluid"},
            {"label": "Centrifugal Pump", "comment": "A pump that uses rotational energy"}
          ],
          "object_properties": [],
          "data_properties": [],
          "subclass_of": [{"sub": "Centrifugal Pump", "super": "Pump"}],
          "disjoint_with": [],
          "equivalent_class": []
        }
        """;

    private static string ProposeReply(Guid chunkId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "Impeller",
            "language": "en",
            "alternate_labels": [],
            "description": "Rotating component of a centrifugal pump",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "explicit component in source",
            "source_chunk_ids": ["{{chunkId}}"]
          }]
        }
        """;

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly IBlobStore _blobs;

    private StoreWrapper Store { get; }

    private KsContext Ks { get; } = new(GraphIri, BaseIri);

    private ExtractionJobStore Jobs { get; }

    private FakeChat FakeChat { get; } = new();

    private ServiceProvider Services { get; }

    private ExtractionOrchestrator Orchestrator { get; }

    private ExtractionRequest Request { get; }

    /// <summary>Guid PK of the fixture chunk the agent's prompt will quote.</summary>
    private Guid ChunkId { get; }

    public ExtractionOrchestratorTerminologyPipelineE2ETests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "isestudio-term-dag-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        Store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        ChunkId = SeedKnowledgeSystem();

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
            FileName: "term-dag.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 1);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task TerminologyPipeline_RunsViaScopeResolution_AndQueuesProposals()
    {
        // Layer 1: TBox extraction produces two classes; layer 2: the
        // deterministic sync seals the scheme (now via the Dovetail
        // StaleMapping → EntitySync → Alias → Broader segments); layer 3:
        // the ProposalStep folds the terminology agent's accepted row.
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ProposeReply(ChunkId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.True(finished.Status == "completed",
            $"Expected completed but got {finished.Status}: {finished.Error} {finished.Log}");
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));

        // The ProposalStep folded one accepted proposal into the job row.
        Assert.Equal(1, finished.TerminologyProposals);

        // The proposal itself landed on the database (proves the DAG's
        // agent pass actually ran, not just the count was synthesised).
        await using var db = _contexts.CreateDbContext();
        var rows = await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("create", row.Action);
        Assert.Equal("Impeller", row.Term);
        Assert.Equal("pending", row.Status);
    }

    // ------------------------------------------------------------------
    // Helpers — copied verbatim from TerminologyAgentOrchestrationTests
    // (same seeding, same build, plus the Dovetail registrations).
    // ------------------------------------------------------------------

    private void SeedTBox()
    {
        var quads = SchemaBuilder.BuildMutation(
            BaseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Pump", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Ks.TBoxGraph);
        Store.AddQuads(new OntoNamedNode(Ks.TBoxGraph), quads);
    }

    private Guid SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = "term-dag-llm",
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
            Name = "Term DAG fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        const string text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "pump.txt",
            Folder = "/",
            ParseStatus = "parsed",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        return chunk.Id;
    }

    private static string PutDocument(IBlobStore blobs)
    {
        var text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }

    /// <summary>
    /// Build the orchestrator's scope factory — the TerminologyAgentOrchestrationTests
    /// BuildServices plus:
    /// <list type="bullet">
    /// <item><c>AddLogging()</c> — the §7/§8 step factories resolve
    /// <c>ILogger&lt;T&gt;</c>;</item>
    /// <item><c>AddDovetailPipelines()</c> — makes the scope resolve the
    /// TerminologyPipeline (and, since Slice 3 R2, the AgentChainPipeline);</item>
    /// <item><c>TerminologyService</c> — the pass steps' only dependency
    /// (a second instance over the same store; stateless wrapper, so
    /// behavior-equivalent to the orchestrator's own);</item>
    /// <item>the agent-chain interface forwarders — with AddDovetailPipelines
    /// in the container, RunAgentChainAsync resolves AgentChainPipeline from
    /// the scope, and its §7 factories need IConflictAgent / IStructureAgent /
    /// IKnowledgeStatsService to construct real steps (missing interfaces
    /// would yield null steps and a mid-job NRE).</item>
    /// </list>
    /// </summary>
    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton(Store);
        services.AddSingleton(Jobs);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new ISEStudioOptions
        {
            TerminologySuggestionMaxChunks = 10,
            TerminologySuggestDuringExtraction = true,
        }));
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        services.AddScoped<IConflictAgent, ConflictAgent>();
        services.AddScoped<StructureAgent>();
        services.AddScoped<IStructureAgent, StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<KnowledgeStatsService>();
        services.AddScoped<IKnowledgeStatsService, KnowledgeStatsService>();
        services.AddScoped<TerminologyAgent>();
        services.AddSingleton(new TerminologyService(Store));
        services.AddDovetailPipelines();
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
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(Store),
            new PromptSnapshotService(),
            new ExtractionMerger(Store),
            Store,
            TimeProvider.System,
            Options.Create(new ISEStudioOptions
            {
                TerminologySuggestDuringExtraction = true,
            }),
            verify: null,
            scopes: scopes);

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
