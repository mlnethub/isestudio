using Dovetail;
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
using ISEStudio.Tests.Extraction.Dovetail.Adapters;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

/// <summary>
/// Slice 5 Task 4: pin the <c>pipeline.ExecuteAsync(state, ct)</c>
/// contract. R13 mandates the return type is <see cref="TerminologyCarry"/>
/// (not <see cref="JobResult"/>) — the router is the only place the
/// final carry gets projected into <see cref="JobResult"/>. R7 mandates
/// the canonical 6-segment chain order; this class verifies a real run
/// through the chain reaches <see cref="TerminologyCarry"/> with the
/// expected <see cref="JobState"/>.
///
/// <para>Each test seeds a job row in the SQLite-backed
/// <see cref="ExtractionJobStore"/> so the orchestrator's
/// <see cref="ExtractionOrchestrator.RunLayerAsync"/>
/// <c>UpdateProgressAsync</c> call (its first DB hit) finds a row to
/// update. Without the seed the chain would throw
/// <c>InvalidOperationException: Sequence contains no elements</c> at
/// the very first phase-runner invocation.</para>
/// </summary>
public sealed class JobPipelineExecutionTests : IDisposable
{
    private const string GraphIri = "http://test.local/ks/exec";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly StoreWrapper _store;
    private readonly ExtractionJobStore _jobs;
    private readonly ExtractionOrchestrator _orchestrator;

    public JobPipelineExecutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-jobexec-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));
        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        _jobs = new ExtractionJobStore(_contexts, TimeProvider.System);

        _orchestrator = new ExtractionOrchestrator(
            _jobs,
            new LocalCasBlobStore(Path.Combine(_root, "blobs")),
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            new FakeChatClientFactory(),
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
    public async Task TBoxOnlyPipeline_ExecuteAsync_ReturnsTerminologyCarry_AndInputStateFoldsThrough()
    {
        var job = await _jobs.CreateAsync(_ksId, "tbox", "fake-model", new[] { 1 }, 1, CancellationToken.None);
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

        var input = BuildInput(JobKind.TBoxOnly, job.Id);

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        // R13: pipeline.ExecuteAsync returns TerminologyCarry, NOT JobResult.
        Assert.NotNull(result);
        Assert.Equal(input.JobId, result.State.JobId);
        Assert.Equal(JobKind.TBoxOnly, result.State.Kind);
        Assert.Equal(input.Chat, result.State.Chat);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ABoxOnlyPipeline_ExecuteAsync_ReturnsTerminologyCarry()
    {
        var job = await _jobs.CreateAsync(_ksId, "abox", "fake-model", new[] { 1 }, 1, CancellationToken.None);
        var pipeline = new ABoxOnlyJobPipeline(
            noOpTBox: new NoOpSegment<JobState, TBoxLayerCarry>(s => new TBoxLayerCarry(s)),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            noOpCorpus: new NoOpSegment<AgentCarry, CorpusCarry>(s => new CorpusCarry(s.State)),
            noOpHierarchy: new NoOpSegment<CorpusCarry, HierarchyCarry>(s => new HierarchyCarry(s.State)),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(_orchestrator), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(_orchestrator), carry => carry.State));

        var input = BuildInput(JobKind.ABoxOnly, job.Id);

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(input.JobId, result.State.JobId);
        Assert.Equal(JobKind.ABoxOnly, result.State.Kind);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task CombinedPipeline_ExecuteAsync_ReturnsTerminologyCarry_AllSixSegmentsFire()
    {
        var job = await _jobs.CreateAsync(_ksId, "combined", "fake-model", new[] { 1 }, 2, CancellationToken.None);
        var pipeline = new CombinedJobPipeline(
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

        var input = BuildInput(JobKind.Combined, job.Id);

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(input.JobId, result.State.JobId);
        Assert.Equal(JobKind.Combined, result.State.Kind);
    }

    public void Dispose()
    {
        _store.Dispose();
        _contexts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Build a <see cref="JobState"/> with all four R11 closure fields
    /// populated against the seeded <paramref name="jobId"/>. Empty
    /// chunks / perChunk — the orchestrator's
    /// <see cref="ExtractionOrchestrator.RunLayerAsync"/>
    /// for-loop body never executes on zero chunks, so the test only
    /// exercises the pipeline shape contract (R13), not extraction
    /// content.
    /// </summary>
    private JobState BuildInput(JobKind kind, Guid jobId)
    {
        var ks = new KsContext(GraphIri, BaseIri);
        var request = new ExtractionRequest(
            KnowledgeSystemId: _ksId,
            BlobSha: string.Empty,
            FileName: "exec.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 2);
        return JobState.From(new JobInput(
            JobId: jobId,
            KnowledgeSystemId: _ksId,
            ChunkIds: Array.Empty<int>(),
            Chat: null!,
            Kind: kind,
            InitialVocabulary: null,
            CancellationToken: CancellationToken.None,
            KsContext: ks,
            Request: request,
            Chunks: Array.Empty<ChunkSpan>(),
            PerChunk: Array.Empty<ChunkVerifyOutcome>()));
    }

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Exec fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
}
