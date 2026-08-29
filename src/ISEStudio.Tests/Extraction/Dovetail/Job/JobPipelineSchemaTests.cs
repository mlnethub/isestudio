using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
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
/// Slice 5 Task 4: pin the canonical 6-segment Job pipeline shape. Each
/// <see cref="XxxJobPipeline"/> is <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c>
/// (R13) and exposes the exact step-slot assignments the design doc
/// mandates (R7 / R8). Also hosts the shared
/// <see cref="JobTestOrchestratorFactory"/> the other Job pipeline test
/// classes use to build a real <see cref="ExtractionOrchestrator"/> (the
/// step ctors null-check the orchestrator reference, so a null! stand-in
/// is not enough).
/// </summary>
public sealed class JobPipelineSchemaTests
{
    [Fact]
    [Trait("Category", "Extraction")]
    public void TBoxOnlyJobPipeline_ImplementsCanonicalJobStateToTerminologyShape()
    {
        using var fx = new JobTestOrchestratorFactory();
        var orch = fx.Create();

        var pipeline = new TBoxOnlyJobPipeline(
            tboxLayer: new TBoxLayerStep(orch),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(orch), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(orch), carry => carry.State),
            noOpABox: new NoOpSegment<HierarchyCarry, ABoxLayerCarry>(s => new ABoxLayerCarry(s.State)),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

        Assert.True(pipeline is IPipeline<JobState, TerminologyCarry>);
        Assert.Equal(typeof(TBoxLayerStep), pipeline.TBoxLayer.GetType());
        // NoOp substitution slots: the agent-chain slot and the ABox layer
        // slot are NoOpSegment<,> — concrete generic instances, not real
        // step classes.
        Assert.IsType<NoOpSegment<TBoxLayerCarry, AgentCarry>>(pipeline.NoOpAgent);
        Assert.IsType<NoOpSegment<HierarchyCarry, ABoxLayerCarry>>(pipeline.NoOpABox);
        // Real step slots are wrapped in ChainAdapter so the 2-arity
        // Task 3 step shape fits the 3-arity pipeline slot.
        Assert.IsType<ChainAdapter<JobState, AgentCarry, CorpusCarry>>(pipeline.Corpus);
        Assert.IsType<ChainAdapter<JobState, CorpusCarry, HierarchyCarry>>(pipeline.Hierarchy);
        Assert.IsType<ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>>(pipeline.Terminology);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void ABoxOnlyJobPipeline_SubstitutesNoOpForAllPreABoxSlots()
    {
        using var fx = new JobTestOrchestratorFactory();
        var orch = fx.Create();

        var pipeline = new ABoxOnlyJobPipeline(
            noOpTBox: new NoOpSegment<JobState, TBoxLayerCarry>(s => new TBoxLayerCarry(s)),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            noOpCorpus: new NoOpSegment<AgentCarry, CorpusCarry>(s => new CorpusCarry(s.State)),
            noOpHierarchy: new NoOpSegment<CorpusCarry, HierarchyCarry>(s => new HierarchyCarry(s.State)),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(orch), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

        Assert.True(pipeline is IPipeline<JobState, TerminologyCarry>);
        Assert.IsType<NoOpSegment<JobState, TBoxLayerCarry>>(pipeline.NoOpTBox);
        Assert.IsType<NoOpSegment<TBoxLayerCarry, AgentCarry>>(pipeline.NoOpAgent);
        Assert.IsType<NoOpSegment<AgentCarry, CorpusCarry>>(pipeline.NoOpCorpus);
        Assert.IsType<NoOpSegment<CorpusCarry, HierarchyCarry>>(pipeline.NoOpHierarchy);
        Assert.IsType<ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>>(pipeline.ABoxLayer);
        Assert.IsType<ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>>(pipeline.Terminology);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void CombinedJobPipeline_WiresAllSixRealStepClasses()
    {
        using var fx = new JobTestOrchestratorFactory();
        var orch = fx.Create();

        var pipeline = new CombinedJobPipeline(
            tboxLayer: new TBoxLayerStep(orch),
            agent: new ChainAdapter<JobState, TBoxLayerCarry, AgentCarry>(
                new AgentStep(orch), carry => carry.State),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(orch), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(orch), carry => carry.State),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(orch), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

        Assert.True(pipeline is IPipeline<JobState, TerminologyCarry>);
        Assert.Equal(typeof(TBoxLayerStep), pipeline.TBoxLayer.GetType());
        // Real step slots after the first are wrapped in ChainAdapter.
        Assert.IsType<ChainAdapter<JobState, TBoxLayerCarry, AgentCarry>>(pipeline.Agent);
        Assert.IsType<ChainAdapter<JobState, AgentCarry, CorpusCarry>>(pipeline.Corpus);
        Assert.IsType<ChainAdapter<JobState, CorpusCarry, HierarchyCarry>>(pipeline.Hierarchy);
        Assert.IsType<ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>>(pipeline.ABoxLayer);
        Assert.IsType<ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>>(pipeline.Terminology);
    }
}

/// <summary>
/// Shared <see cref="ExtractionOrchestrator"/> factory for the Slice 5
/// Job pipeline tests. The orchestrator ctor null-checks all 13 mandatory
/// dependencies, so the step ctor-shape tests need a real (if inert)
/// instance rather than <c>null!</c> arguments. The factory creates a
/// fresh temp store per instance; dispose to release the store and the
/// temp directory.
/// </summary>
internal sealed class JobTestOrchestratorFactory : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = new();
    private ExtractionOrchestrator? _orchestrator;

    public JobTestOrchestratorFactory(string prefix = "isestudio-jobpipe")
    {
        _root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public ExtractionOrchestrator Create()
    {
        if (_orchestrator is not null) return _orchestrator;

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

    /// <summary>Direct access to the test temp store (some tests seed it).</summary>
    public StoreWrapper Store => (StoreWrapper)_disposables[0];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
