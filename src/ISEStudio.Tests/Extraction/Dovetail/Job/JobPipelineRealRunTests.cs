using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Extraction;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

/// <summary>
/// Slice 5 Task 4: end-to-end happy-path gate. Builds a real
/// <see cref="ExtractionOrchestrator"/> + the TBox-only Job pipeline +
/// router, runs the pipeline against three synthetic chunks backed by
/// the <see cref="FakeChat"/> canned deltas, and asserts the run
/// produced a successful <see cref="JobResult"/> with
/// <c>ProcessedChunks == 3</c>. This is the first real execution of the
/// full Job pipeline; previous slices ran the TBox / ABox / agent /
/// terminology sub-DAGs in isolation.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class JobPipelineRealRunTests : IDisposable
{
    private const string GraphIri = "http://test.local/ks/jobreal";
    private const string BaseIri = GraphIri + "/onto#";
    private const string SourceText = "Animal kingdom; Dog is an Animal; Collar worn by a Dog.";

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly FakeChat _chat = new();
    private readonly FakeChatClientFactory _chatFactory = new();
    private readonly StoreWrapper _store;
    private readonly ExtractionJobStore _jobs;
    private readonly ExtractionOrchestrator _orchestrator;

    public JobPipelineRealRunTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-jobreal-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));
        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        _chatFactory.UseClient(_chat);
        _jobs = new ExtractionJobStore(_contexts, TimeProvider.System);

        _orchestrator = new ExtractionOrchestrator(
            _jobs,
            new LocalCasBlobStore(Path.Combine(_root, "blobs")),
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            _chatFactory,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(_store),
            new PromptSnapshotService(),
            new ExtractionMerger(_store),
            _store,
            TimeProvider.System);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task TBoxOnlyJobPipeline_RealRun_ThreeChunks_SucceedsAndProcessedChunksEquals3()
    {
        // 1. Seed a job row so the orchestrator's per-chunk
        //    UpdateProgressAsync / RecordTBoxMergeAsync writes land on a
        //    real row. The pipeline uses the JobState's JobId, not the
        //    orchestrator's CreateAsync, so we create one here.
        var chunkIds = new[] { 1, 2, 3 };
        var job = await _jobs.CreateAsync(
            knowledgeSystemId: _ksId,
            kind: "tbox",
            model: "fake-model",
            chunkIds: chunkIds,
            totalChunks: chunkIds.Length,
            cancellationToken: CancellationToken.None);

        // 2. Build the canonical TBoxOnly pipeline + the router.
        var pipeline = new TBoxOnlyJobPipeline(
            tboxLayer: new TBoxLayerStep(_orchestrator),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(_orchestrator), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(_orchestrator), carry => carry.State),
            noOpABox: new NoOpSegment<HierarchyCarry, ABoxLayerCarry>(s => new ABoxLayerCarry(s.State)),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(_orchestrator), carry => carry.State));
        var router = new JobPipelineRouter(pipeline, BuildABoxOnlyPipeline(), BuildCombinedPipeline());

        // 3. Enqueue 3 valid TBox deltas — the TBox layer's extractor is
        //    _tbox.ExtractAsync (no verify pass when _verify is null,
        //    which the test orchestrator's no-DI ctor is), so one chat
        //    call per chunk is enough.
        _chat.EnqueueValidDeltas(3);

        // 4. Build a 3-chunk JobInput with the real KsContext / Request
        //    and a 3-chunk ChunkSpan list.
        var ksContext = new KsContext(GraphIri, BaseIri);
        var request = new ExtractionRequest(
            KnowledgeSystemId: _ksId,
            BlobSha: string.Empty,
            FileName: "realrun.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 2);
        var chunks = new[]
        {
            new ChunkSpan(1, SourceText, 0, SourceText.Length, 10),
            new ChunkSpan(2, SourceText, 0, SourceText.Length, 10),
            new ChunkSpan(3, SourceText, 0, SourceText.Length, 10),
        };
        var input = new JobInput(
            JobId: job.Id,
            KnowledgeSystemId: _ksId,
            ChunkIds: chunkIds,
            Chat: _chat,
            Kind: JobKind.TBoxOnly,
            InitialVocabulary: null,
            CancellationToken: CancellationToken.None,
            KsContext: ksContext,
            Request: request,
            Chunks: chunks,
            PerChunk: Array.Empty<ChunkVerifyOutcome>());

        // 5. Run the pipeline.
        var result = await router.ExecuteAsync(input, CancellationToken.None);

        // 6. The gate: success + every chunk processed. TBox-only does
        //    not refresh ProcessedChunks via the agent / terminology
        //    phase-runner, so the TBox layer's progress counter (which
        //    advances to ChunkIds.Count when the for-loop finishes) is
        //    the final value.
        Assert.True(result.Succeeded, $"Run failed: {result.Error}");
        Assert.Equal(3, result.ProcessedChunks);
    }

    public void Dispose()
    {
        _chatFactory.Reset();
        _chat.Release();
        _store.Dispose();
        _contexts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- Pipeline builders (sibling pipelines the router ctor needs) -

    private ABoxOnlyJobPipeline BuildABoxOnlyPipeline() =>
        new(
            noOpTBox: new NoOpSegment<JobState, TBoxLayerCarry>(s => new TBoxLayerCarry(s)),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            noOpCorpus: new NoOpSegment<AgentCarry, CorpusCarry>(s => new CorpusCarry(s.State)),
            noOpHierarchy: new NoOpSegment<CorpusCarry, HierarchyCarry>(s => new HierarchyCarry(s.State)),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(_orchestrator), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(_orchestrator), carry => carry.State));

    private CombinedJobPipeline BuildCombinedPipeline() =>
        new(
            tboxLayer: new TBoxLayerStep(_orchestrator),
            agent: new ChainAdapter<JobState, TBoxLayerCarry, AgentCarry>(
                new AgentStep(_orchestrator), carry => carry.State),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(_orchestrator), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(_orchestrator), carry => carry.State),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(_orchestrator), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(_orchestrator), carry => carry.State));

    // ---- DB seeding ---------------------------------------------------

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Real-run fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
}
