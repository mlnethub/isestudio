using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

/// <summary>
/// Slice 5 Task 4: <see cref="JobPipelineRouter"/> Kind dispatch + DI
/// contract. R14 confirms the router returns <see cref="JobResult"/> from
/// the per-kind pipeline; the schema is
/// <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c> inside the router
/// (R13).
/// </summary>
public sealed class JobPipelineRouterTests : IDisposable
{
    private readonly JobTestOrchestratorFactory _factory = new();

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_ResolvesAllThreePerKindPipelines()
    {
        // MS.DI semantics: GetService<X> returns null for unregistered types.
        // After AddSingleton, all three pipelines + the router must resolve.
        var services = new ServiceCollection();
        services.AddSingleton(BuildTBoxOnlyPipeline());
        services.AddSingleton(BuildABoxOnlyPipeline());
        services.AddSingleton(BuildCombinedPipeline());
        services.AddSingleton<JobPipelineRouter>();
        using var sp = services.BuildServiceProvider();

        var router = sp.GetService<JobPipelineRouter>();

        Assert.NotNull(router);
        Assert.NotNull(sp.GetService<TBoxOnlyJobPipeline>());
        Assert.NotNull(sp.GetService<ABoxOnlyJobPipeline>());
        Assert.NotNull(sp.GetService<CombinedJobPipeline>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_GetServiceReturnsNull_WhenPipelineNotRegistered()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        // Slice 4 R2 ruling: GetService returns null for unregistered types.
        Assert.Null(sp.GetService<JobPipelineRouter>());
        Assert.Null(sp.GetService<TBoxOnlyJobPipeline>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_GetRequiredServiceThrows_WhenPipelineMissingDependency()
    {
        // Slice 4 R2 ruling: registered types with missing dependencies
        // throw InvalidOperationException from the DI container.
        var services = new ServiceCollection();
        services.AddSingleton<JobPipelineRouter>(); // no pipeline deps registered
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<JobPipelineRouter>());
    }

    public void Dispose() => _factory.Dispose();

    // ---- Pipeline builders ---------------------------------------------

    private TBoxOnlyJobPipeline BuildTBoxOnlyPipeline()
    {
        var orch = _factory.Create();
        return new TBoxOnlyJobPipeline(
            tboxLayer: new TBoxLayerStep(orch),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(orch), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(orch), carry => carry.State),
            noOpABox: new NoOpSegment<HierarchyCarry, ABoxLayerCarry>(s => new ABoxLayerCarry(s.State)),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));
    }

    private ABoxOnlyJobPipeline BuildABoxOnlyPipeline()
    {
        var orch = _factory.Create();
        return new ABoxOnlyJobPipeline(
            noOpTBox: new NoOpSegment<JobState, TBoxLayerCarry>(s => new TBoxLayerCarry(s)),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            noOpCorpus: new NoOpSegment<AgentCarry, CorpusCarry>(s => new CorpusCarry(s.State)),
            noOpHierarchy: new NoOpSegment<CorpusCarry, HierarchyCarry>(s => new HierarchyCarry(s.State)),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(orch), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));
    }

    private CombinedJobPipeline BuildCombinedPipeline()
    {
        var orch = _factory.Create();
        return new CombinedJobPipeline(
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
    }
}
