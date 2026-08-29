using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class StatsRefreshStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullStats_ReturnsAgentChainResult()
    {
        var step = new StatsRefreshStep(null, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<string>());
        var structure = new StructureAttachResult(Array.Empty<string>());
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_RefreshesAndBundles()
    {
        var fakeStats = new FakeKnowledgeStatsService();
        var step = new StatsRefreshStep(fakeStats, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var triage = new ConflictTriageResult(new[] { "triage1" });
        var structure = new StructureAttachResult(new[] { "attach1", "attach2" });
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
        Assert.Equal(1, fakeStats.RefreshCallCount);
        Assert.Equal(input.KnowledgeSystemId, fakeStats.LastKnowledgeSystemId);
    }

    [Fact]
    public async Task ExecuteAsync_StatsThrows_FailsSoft_StillReturnsResult()
    {
        var fakeStats = new FakeKnowledgeStatsService { ThrowOnRefresh = true };
        var step = new StatsRefreshStep(fakeStats, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var triage = new ConflictTriageResult(new[] { "triage1" });
        var structure = new StructureAttachResult(new[] { "attach1", "attach2" });
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
    }
}

internal sealed class FakeKnowledgeStatsService : IKnowledgeStatsService
{
    public int RefreshCallCount { get; private set; }
    public Guid LastKnowledgeSystemId { get; private set; }
    public bool ThrowOnRefresh { get; init; }

    public Task RefreshAsync(Guid knowledgeSystemId, CancellationToken cancellationToken)
    {
        RefreshCallCount++;
        LastKnowledgeSystemId = knowledgeSystemId;
        if (ThrowOnRefresh) throw new InvalidOperationException("test-induced stats failure");
        return Task.CompletedTask;
    }
}