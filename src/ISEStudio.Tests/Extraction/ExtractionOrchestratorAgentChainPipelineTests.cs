using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction.Dovetail.AgentChain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// DI-level tests for the Dovetail agent chain pipeline resolution through
/// the same registration surface the orchestrator uses
/// (<see cref="DovetailPipelineRegistrations.AddDovetailPipelines"/>).
/// Mirrors the Task 4 registrations pattern: interface-keyed fakes with no
/// DI deps stand in for the production agents (whose ctors need
/// ISEStudioDbContext / IChatClientFactory / StoreWrapper / OntologyViewBuilder
/// full wiring), exactly like
/// <see cref="DovetailPipelineRegistrationsAgentChainTests"/>.
/// </summary>
public class ExtractionOrchestratorAgentChainPipelineTests
{
    [Fact]
    public void AgentChainPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<IConflictAgent, FakeConflictAgent>();
        services.AddSingleton<IStructureAgent, FakeStructureAgent>();
        services.AddSingleton<IKnowledgeStatsService, FakeKnowledgeStatsService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AgentChainPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.Null(pipeline);
    }
}
