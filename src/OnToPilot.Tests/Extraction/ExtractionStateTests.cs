using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Storage;
using OnToPilot.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// State-machine tests for <see cref="ExtractionOrchestrator"/>: job row
/// lifecycle, live progress, phase sequencing, prompt snapshots, terminology
/// metrics, and — the load-bearing one — RDF/SQL atomicity on merge failure.
///
/// <para>Everything runs against real collaborators (Oxigraph store,
/// <see cref="LocalCasBlobStore"/>, <see cref="DocumentParser"/>,
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
    private readonly Guid _ksId = Guid.NewGuid();

    /// <summary>Oxigraph store under test.</summary>
    private StoreWrapper Store { get; }

    /// <summary>Graph coordinates for the seeded knowledge system.</summary>
    private KsContext Ks { get; } = new(GraphIri, BaseIri);

    /// <summary>Job-row reader/writer the orchestrator and the tests share.</summary>
    private ExtractionJobStore Jobs { get; }

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
        _root = Path.Combine(Path.GetTempPath(), "ontopilot-extraction-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        Store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        var blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        var sha = PutDocument(blobs);

        Jobs = new ExtractionJobStore(_contexts, TimeProvider.System);
        Merger = new FakeMerger(new ExtractionMerger(Store));

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(FakeChat);

        Orchestrator = new ExtractionOrchestrator(
            Jobs,
            blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new OnToPilotOptions())),
            new ABoxExtractionService(Options.Create(new OnToPilotOptions())),
            new TerminologyService(Store),
            new PromptSnapshotService(),
            Merger,
            Store,
            TimeProvider.System);

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
        await Jobs.WaitAsync(job.Id);
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
        await Jobs.WaitAsync(job.Id);

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
        var finished = await Jobs.WaitAsync(job.Id);

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
        var finished = await Jobs.WaitAsync(job.Id);

        var diag = $"{parserDiag} status={finished.Status} error={finished.Error} phase={finished.Phase} " +
                   $"log={finished.Log} chatCalls={FakeChat.CallCount} " +
                   $"classes={ClassCount()} before={before} axiomsAdded={finished.AxiomsAdded}";
        Assert.True(finished.Status == "completed", diag);
        Assert.True(ClassCount() > before, diag);
        Assert.True(finished.AxiomsAdded > 0, diag);
        Assert.Empty(Store.Match(graph: new OntoNamedNode(Ks.ABoxGraph)));
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

        var midway = await PollAsync(job.Id, j => j.ProcessedChunks >= 1);
        Assert.True(midway.TotalChunks > 1, "Fixture document must chunk into more than one span.");
        Assert.True(midway.ProcessedChunks < midway.TotalChunks, "Progress should still be partial while parked.");
        Assert.Equal("running", midway.Status);

        FakeChat.Release();
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
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
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal("abox", finished.Kind);
        Assert.True(finished.IndividualsAdded > 0, "ABox extraction should create individuals.");

        // Instances land in the ABox graph — never in the schema graph.
        Assert.NotEmpty(Store.Match(graph: new OntoNamedNode(Ks.ABoxGraph)));
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
        var finished = await Jobs.WaitAsync(job.Id);

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
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal("both", finished.Kind);

        // The job log is an append-only phase history, so the ordering can be
        // asserted deterministically rather than by racing the Phase column.
        var history = ExtractionJobLog.Phases(finished.Log);
        Assert.Equal(
            new[] { "tbox", "abox", "terminology", "finalizing" },
            history);
        Assert.Equal("finalizing", finished.Phase);

        // Combined runs walk every chunk twice (once per layer).
        Assert.Equal(finished.TotalChunks, finished.ProcessedChunks);
        Assert.NotEmpty(Store.Match(graph: new OntoNamedNode(Ks.ABoxGraph)));
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
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.True(finished.TermsAdded > 0, "Terminology sync should mint concepts for new classes.");
        Assert.Null(finished.TerminologyError);

        // Re-running against the same vocabulary maps rather than re-adds.
        // Python parity (P3-10): terms_mapped counts mappings the PASS
        // performed (fresh creates + adopted unmapped concepts), so an
        // idempotent rerun reports 0 — every entity already has its
        // mapped concept and the loop skips it.
        var second = new TerminologyService(Store).SyncAsync(Ks, CancellationToken.None);
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
            predicateIri: Vocabulary.RdfType.Value,
            objectIri: Vocabulary.OwlClass.Value,
            graphIri: Ks.TBoxGraph).Count;

    /// <summary>Seed a single <c>Person</c> class so the ABox mentions resolve.</summary>
    private void SeedTBox()
    {
        var quads = SchemaBuilder.BuildMutation(
            BaseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: new[] { new PropertyMutation("age", "data", Domain: "Person", Range: "integer") },
                Axioms: Array.Empty<AxiomMutation>()),
            Ks.TBoxGraph);
        Store.AddQuads(new OntoNamedNode(Ks.TBoxGraph), quads);
    }

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Extraction fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
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
