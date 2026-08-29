using ISEStudio.Conflicts;
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class ConflictAgentStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullAgent_ReturnsEmptyTriageLog()
    {
        var step = new ConflictAgentStep(null, NullLogger<ConflictAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var result = await step.ExecuteAsync(input, CancellationToken.None);
        Assert.Empty(result.TriageLog);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsLogEntries()
    {
        var fakeAgent = new FakeConflictAgent(new[] { "entry1", "entry2", "entry3" });
        var step = new ConflictAgentStep(fakeAgent, NullLogger<ConflictAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), "test-model");
        var result = await step.ExecuteAsync(input, CancellationToken.None);
        Assert.Equal(3, result.TriageLog.Count);
        Assert.Equal("entry1", result.TriageLog[0]);
        Assert.Equal(input.KnowledgeSystemId, fakeAgent.LastKsId);
        Assert.True(fakeAgent.LastSkipActiveExtractionGate);
    }
}

internal sealed class FakeConflictAgent : IConflictAgent
{
    private readonly IReadOnlyList<string> _log;

    public FakeConflictAgent(IReadOnlyList<string> log) => _log = log;

    public Guid LastKsId { get; private set; }
    public bool LastSkipActiveExtractionGate { get; private set; }

    public Task<IReadOnlyList<string>> TriageAsync(
        Guid ksId,
        CancellationToken ct,
        string? model = null,
        bool skipActiveExtractionGate = false)
    {
        LastKsId = ksId;
        LastSkipActiveExtractionGate = skipActiveExtractionGate;
        return Task.FromResult(_log);
    }
}