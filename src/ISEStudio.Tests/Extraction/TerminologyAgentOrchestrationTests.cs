using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
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
/// Pipeline-level test for the P3-1 (terminology proposals) wiring: after
/// <see cref="TerminologyService"/> finishes its deterministic sync, the
/// orchestrator must resolve the scoped <see cref="TerminologyAgent"/>,
/// feed it the parsed chunks + scheme IRI, and fold the accepted-row count
/// back into the job row's <c>terminology_proposals</c> column.
///
/// <para>Mirrors the Python backend's
/// <c>backend/app/api/extraction.py:_run_terminology_sync</c> semantic:
/// the agent runs after the deterministic sync, the proposal count ends up
/// on the <see cref="ExtractionJobEntity"/>, and a transient failure
/// leaves the job completed (advisory best-effort).</para>
/// </>
[Collection(ExtractionTestCollection.Name)]
public sealed class TerminologyAgentOrchestrationTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/term-agent";
    private const string BaseIri = GraphIri + "/onto#";

    /// <summary>
    /// Canned TBox delta the extraction layer feeds to the LLM. Two classes
    /// (<c>Pump</c>, <c>Centrifugal Pump</c>) so the deterministic
    /// terminology sync has at least one entity to anchor a concept scheme
    /// on (the agent step is gated on <c>SchemeIri != null</c>).
    /// </summary>
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

    /// <summary>
    /// Valid LLM reply the terminology agent must parse: one <c>create</c>
    /// proposal whose <c>preferred_label</c> / <c>source_chunk_ids</c> line
    /// up with the seeded fixture chunk. <c>source_chunk_ids</c> uses the
    /// chunk's wire-format <see cref="LegacyAddressableEntity.LegacyId"/>
    /// because that is what the agent's filter key compares against.
    /// </summary>
    private static string ProposeReply(long chunkLegacyId) => $$"""
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
            "source_chunk_ids": [{{chunkLegacyId}}]
          }]
        }
        """;

    /// <summary>
    /// LLM reply whose <c>preferred_label</c> does NOT appear in the
    /// cited chunk text. The seeded chunk mentions "impeller" — this
    /// reply proposes "Compressor" verbatim, which the _source_contains
    /// check should reject (parity with Python
    /// <c>terminology_agent._filter_to_supported_labels</c>).
    /// </summary>
    private static string HallucinatedReply(long chunkLegacyId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "Compressor",
            "language": "en",
            "alternate_labels": [],
            "description": "Device that pressurises gas",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "hallucinated term not in source",
            "source_chunk_ids": [{{chunkLegacyId}}]
          }]
        }
        """;

    /// <summary>
    /// LLM reply whose <c>preferred_label</c> matches a corpus term
    /// only via case-insensitive comparison. The seeded chunk text
    /// uses lowercase "pump" — this reply proposes "PUMP" (uppercase),
    /// which the OrdinalIgnoreCase grounding check should accept.
    /// </summary>
    private static string CaseVariantReply(long chunkLegacyId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "PUMP",
            "language": "en",
            "alternate_labels": [],
            "description": "Device that moves fluid",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "case variant of corpus term",
            "source_chunk_ids": [{{chunkLegacyId}}]
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

    /// <summary>Wire-format LegacyId of the fixture chunk the agent's prompt will quote.</summary>
    private long ChunkLegacyId { get; set; }

    public TerminologyAgentOrchestrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "isestudio-term-agent-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        Store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        ChunkLegacyId = SeedKnowledgeSystem();

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
            FileName: "term-agent.txt",
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
    public async Task Terminology_agent_runs_after_sync_and_queues_proposals()
    {
        // Layer 1: TBox extraction produces two classes; layer 2: the
        // deterministic sync seals the scheme; layer 3: the terminology
        // agent (LLM) emits one accepted proposal.
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ProposeReply(ChunkLegacyId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.True(finished.Status == "completed",
            $"Expected completed but got {finished.Status}: {finished.Error} {finished.Log}");
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));

        // The agent step folded one accepted proposal into the job row.
        Assert.Equal(1, finished.TerminologyProposals);

        // The proposal itself landed on the database (proves the scoped
        // agent path actually ran, not just the count was synthesised).
        await using var db = _contexts.CreateDbContext();
        var rows = await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("create", row.Action);
        Assert.Equal("Impeller", row.Term);
        Assert.Equal("terminology-agent", row.ProposedBy);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Terminology_proposals_serialize_as_nonzero_in_wire_shape()
    {
        // P3-4 follow-up gap: the dispatcher's wire field is `terminology_proposals`
        // (JobOut.From + controller SnakeCaseLower). The non-zero path is
        // wired end-to-end via the existing test above; this one explicitly
        // pins the JSON shape so future refactors of the projection (e.g.
        // collapsing it into the dispatcher's anonymous object) cannot
        // silently drop the count without a test failure.
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ProposeReply(ChunkLegacyId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.True(finished.TerminologyProposals > 0,
            $"expected TerminologyProposals > 0, got {finished.TerminologyProposals}");

        // Round-trip the wire projection and prove the count survives
        // serialisation. This is the field the InternalApiFacade hands
        // clients via extraction.get_job.
        var wireOut = ExtractionJobOut.From(finished);
        var wireJson = JsonSerializer.Serialize(wireOut);
        Assert.Contains("\"terminology_proposals\":1", wireJson);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Terminology_agent_short_circuits_when_no_chunks_exist()
    {
        // No chunk is seeded — RunTerminologyAgentAsync must bail before
        // hitting the LLM, so the proposal count stays at zero and no
        // fake-chat reply is consumed. The deterministic sync still
        // produces a SchemeIri, but the empty chunk list is the gate.
        var contextsNoChunk = new SqliteContextFactory();
        SeedKnowledgeSystemNoChunks(contextsNoChunk);
        var servicesNoChunk = BuildServices();
        var orchestrator = BuildOrchestrator(servicesNoChunk.GetRequiredService<IServiceScopeFactory>());

        FakeChat.Enqueue(TBoxDelta);
        // No propose reply enqueued — agent step must not call the LLM.

        var job = await orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(0, finished.TerminologyProposals);

        await using var db = contextsNoChunk.CreateDbContext();
        Assert.Empty(await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId).ToListAsync());

        servicesNoChunk.Dispose();
        contextsNoChunk.Dispose();
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Terminology_agent_drops_proposal_when_term_not_in_cited_chunks()
    {
        // _source_contains grounding check (P3-8, parity with Python
        // `_filter_to_supported_labels`): if the LLM proposes a term
        // that does not literally appear in any cited chunk, the agent
        // must drop it silently rather than write a row the reviewer
        // has no evidence to verify against. The seeded chunk text
        // mentions "impeller" verbatim; this test proposes "Compressor"
        // (NOT in the chunk) and asserts 0 proposals are persisted.
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(HallucinatedReply(ChunkLegacyId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(0, finished.TerminologyProposals);

        await using var db = _contexts.CreateDbContext();
        Assert.Empty(await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Terminology_agent_accepts_proposal_when_term_matches_case_insensitively()
    {
        // The grounding check uses OrdinalIgnoreCase so "PUMP" (uppercase)
        // matches chunk text "pump". Protects against the LLM proposing
        // a normalized-case variant of a corpus term — the reviewer's
        // mental model is "the term string is present", not "exact byte
        // match".
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(CaseVariantReply(ChunkLegacyId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(1, finished.TerminologyProposals);

        await using var db = _contexts.CreateDbContext();
        var row = Assert.Single(await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId).ToListAsync());
        Assert.Equal("PUMP", row.Term);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void SeedTBox()
    {
        // Pre-existing TBox classes so the deterministic sync has something
        // to anchor a concept scheme on even before extraction runs.
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

    private long SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = "term-agent-llm",
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
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term agent fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // A single chunk whose text mentions "impeller" verbatim so the
        // agent's grounding checks would have something to quote if it
        // ran them — even though TryBuildProposal in this slice only
        // validates source_chunk_ids membership, leaving room to grow.
        const string text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("document"),
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
            LegacyId = TestLegacyIds.Next("chunk"),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        return chunk.LegacyId;
    }

    /// <summary>
    /// Variant of <see cref="SeedKnowledgeSystem"/> that creates the
    /// knowledge system + provider but no chunks — used by the
    /// short-circuit test to prove the agent step is gated on chunk
    /// availability.
    /// </summary>
    private void SeedKnowledgeSystemNoChunks(SqliteContextFactory factory)
    {
        using var db = factory.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = "term-agent-llm-empty",
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
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term agent empty fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static string PutDocument(IBlobStore blobs)
    {
        var text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }

    /// <summary>
    /// Build the orchestrator's scope factory. <see cref="TerminologyAgent"/>
    /// is registered Scoped (matches the production lifetime at
    /// <c>ExtractionServiceCollectionExtensions:31</c>) so the EF context
    /// the agent sees is the one we resolve alongside it — the same
    /// pattern <c>RunAgentChainAsync</c> uses for the conflict / structure
    /// services.
    ///
    /// <para>The orchestrator's <c>RunAgentChainAsync</c> always runs in
    /// production and resolves <c>ConflictService</c> / <c>ConflictAgent</c> /
    /// <c>StructureAgent</c> / <c>KnowledgeStatsService</c> from this scope,
    /// so they all have to be registered even for a terminology-agent
    /// fixture — failing to register them turns <c>RunAgentChainAsync</c>
    /// into an uncaught activation error and the whole job flips to
    /// <c>failed</c>. Mirror <see cref="ExtractionAgentChainTests.BuildServices"/>.</para>
    /// </summary>
    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
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
        services.AddScoped<LegacyIdAllocator>();
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        services.AddScoped<StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<KnowledgeStatsService>();
        services.AddScoped<TerminologyAgent>();
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