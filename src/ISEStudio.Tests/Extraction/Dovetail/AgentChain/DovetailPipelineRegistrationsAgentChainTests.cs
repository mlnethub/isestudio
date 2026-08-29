using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class DovetailPipelineRegistrationsAgentChainTests
{
    [Fact]
    public void ConflictAgentStep_IsResolvable_WhenAgentRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<IConflictAgent, FakeConflictAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<ConflictAgentStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void StructureAgentStep_IsResolvable_WhenAgentRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<IStructureAgent, FakeStructureAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<StructureAgentStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void StatsRefreshStep_IsResolvable_WhenStatsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<IKnowledgeStatsService, FakeKnowledgeStatsService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<StatsRefreshStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void AllAgentChainSteps_ResolveNull_WhenUnderlyingServicesNotRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<ConflictAgentStep>());
        Assert.Null(sp.GetService<StructureAgentStep>());
        Assert.Null(sp.GetService<StatsRefreshStep>());
    }

    [Fact]
    public void AgentChainPipeline_IsResolvable_WhenAllStepsResolve()
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
}

/// <summary>
/// Minimal interface implementation with no DI deps. Used to verify the
/// factory pattern resolves the step when the underlying service is
/// registered, without requiring the production concrete classes (which
/// have complex ctor deps like ISEStudioDbContext / IChatClientFactory /
/// StoreWrapper / OntologyViewBuilder that need full DI wiring).
/// </summary>
internal sealed class FakeConflictAgent : IConflictAgent
{
    public Task<IReadOnlyList<string>> TriageAsync(
        Guid knowledgeSystemId,
        CancellationToken cancellationToken,
        string? model = null,
        bool skipActiveExtractionGate = false)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal sealed class FakeStructureAgent : IStructureAgent
{
    public Task<IReadOnlyList<string>> AttachIsolatedAsync(
        Guid knowledgeSystemId,
        string? model = null,
        CancellationToken cancellationToken = default,
        bool skipActiveExtractionGate = false)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal sealed class FakeKnowledgeStatsService : IKnowledgeStatsService
{
    public Task RefreshAsync(Guid knowledgeSystemId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
