using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Extraction.Dovetail.Adapters;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

/// <summary>
/// Slice 5 Task 3: shape + short-circuit behaviour of the Dovetail Job
/// phase segments. The phase runners themselves stay covered by the
/// existing orchestrator suites — these tests pin the segment contracts
/// (ctor, <c>IPipelineSegment</c> shape, <see cref="JobState.ShouldSkipRemaining"/>
/// guard) and the per-phase try/catch adapter.
/// </summary>
public sealed class JobStepTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private ExtractionOrchestrator? _orchestrator;
    private string? _root;

    private static JobState EmptyState() => JobState.From(new JobInput(
        JobId: Guid.NewGuid(),
        KnowledgeSystemId: Guid.NewGuid(),
        ChunkIds: new[] { 1 },
        Chat: null!,
        Kind: JobKind.TBoxOnly,
        InitialVocabulary: null,
        CancellationToken: CancellationToken.None,
        KsContext: new KsContext("http://test.local/ks/step", "http://test.local/ks/step#"),
        Request: new ExtractionRequest(
            KnowledgeSystemId: Guid.NewGuid(),
            BlobSha: string.Empty,
            FileName: "step.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 2),
        Chunks: Array.Empty<ChunkSpan>(),
        PerChunk: Array.Empty<ChunkVerifyOutcome>()));

    /// <summary>
    /// The orchestrator ctor null-checks all 13 mandatory dependencies, so
    /// the segments' ctor-shape tests need a real (if inert) instance
    /// rather than <c>default!</c> arguments. Built lazily so only the
    /// tests that need one pay for the temp store.
    /// </summary>
    private ExtractionOrchestrator CreateOrchestrator()
    {
        if (_orchestrator is not null) return _orchestrator;

        _root = Path.Combine(Path.GetTempPath(), "isestudio-jobstep-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        var store = new StoreWrapper(Path.Combine(_root, "store"));
        _disposables.Add(store);
        var contexts = new SqliteContextFactory();
        _disposables.Add(contexts);

        return _orchestrator = new ExtractionOrchestrator(
            new ExtractionJobStore(contexts, TimeProvider.System),
            new LocalCasBlobStore(Path.Combine(_root, "blobs")),
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            new FakeChatClientFactory(),
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(store),
            new PromptSnapshotService(),
            new ExtractionMerger(store),
            store,
            TimeProvider.System);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        if (_root is not null)
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task NoOpAgentStep_ReturnsInputUnchanged()
    {
        var step = NoOpAgentStep.Create();
        var state = EmptyState() with { ProcessedChunks = 7 };

        var result = await step.ExecuteAsync(state, CancellationToken.None);

        Assert.Same(state, result.State);
        Assert.Equal(7L, result.State.ProcessedChunks);
        Assert.True(result.State.Succeeded);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task PerPhaseCatchStep_InnerSucceeds_ReturnsInnerOutput()
    {
        var state = EmptyState();
        var step = new PerPhaseCatchStep<AgentCarry>(NoOpAgentStep.Create(), s => new AgentCarry(s));

        var result = await step.ExecuteAsync(state, CancellationToken.None);

        Assert.Same(state, result.State);
        Assert.True(result.State.Succeeded);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task PerPhaseCatchStep_InnerThrows_ReturnsStateWithError()
    {
        var step = new PerPhaseCatchStep<AgentCarry>(
            new ThrowingSegment<JobState, AgentCarry>("phase-failed"),
            s => new AgentCarry(s));

        var result = await step.ExecuteAsync(EmptyState(), CancellationToken.None);

        Assert.False(result.State.Succeeded);
        Assert.True(result.State.ShouldSkipRemaining);
        Assert.Equal("phase-failed", result.State.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task PerPhaseCatchStep_OperationCanceledException_Rethrows()
    {
        var step = new PerPhaseCatchStep<AgentCarry>(
            new InlineSegment<JobState, AgentCarry>((_, _) => throw new OperationCanceledException()),
            s => new AgentCarry(s));

        // The adapter's explicit `catch (OperationCanceledException) { throw; }`
        // clause keeps a cancelled job cancelled rather than recording it as
        // a phase failure, so the OCE surfaces instead of an error carry.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(EmptyState(), new CancellationToken(canceled: true)));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task CorpusStep_SkipRemaining_ReturnsInput()
    {
        var step = new CorpusStep(CreateOrchestrator());
        var state = EmptyState() with { Error = "previous-failed" };

        var result = await step.ExecuteAsync(state, CancellationToken.None);

        Assert.Same(state, result.State);
        Assert.Equal("previous-failed", result.State.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task HierarchyStep_SkipRemaining_ReturnsInput()
    {
        var step = new HierarchyStep(CreateOrchestrator());
        var state = EmptyState() with { Error = "previous-failed" };

        var result = await step.ExecuteAsync(state, CancellationToken.None);

        Assert.Same(state, result.State);
        Assert.Equal("previous-failed", result.State.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void LayerSteps_TBoxAndABox_AcceptJobStateInput()
    {
        var orchestrator = CreateOrchestrator();

        var tbox = new TBoxLayerStep(orchestrator);
        var abox = new ABoxLayerStep(orchestrator);

        // Executing the real layer needs the per-job closure Task 4 wires,
        // so this pins construction + the segment shapes only.
        Assert.True(tbox is IPipelineSegment<JobState, TBoxLayerCarry>);
        Assert.True(abox is IPipelineSegment<JobState, ABoxLayerCarry>);

        // R3: the ABox layer resumes the combined job's progress counter.
        var combined = EmptyState() with { Kind = JobKind.Combined, ChunkIds = new[] { 1, 2, 3 } };
        Assert.Equal(3, ABoxLayerStep.BaseProcessedOffset(combined));
        Assert.Equal(0, ABoxLayerStep.BaseProcessedOffset(EmptyState() with { Kind = JobKind.ABoxOnly }));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AgentStep_And_TerminologyStep_AcceptOrchestrator()
    {
        var orchestrator = CreateOrchestrator();

        var agent = new AgentStep(orchestrator);
        var terminology = new TerminologyStep(orchestrator);

        Assert.True(agent is IPipelineSegment<JobState, AgentCarry>);
        Assert.True(terminology is IPipelineSegment<JobState, TerminologyCarry>);
        Assert.Throws<ArgumentNullException>(() => new AgentStep(null!));
    }
}
