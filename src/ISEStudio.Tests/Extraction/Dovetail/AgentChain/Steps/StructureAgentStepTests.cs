using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class StructureAgentStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullAgent_ReturnsEmptyAttachLog()
    {
        var step = new StructureAgentStep(null, NullLogger<StructureAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<string>());
        var result = await step.ExecuteAsync(input, triage, CancellationToken.None);
        Assert.Empty(result.AttachLog);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsLogEntries()
    {
        var fakeAgent = new FakeStructureAgent(new[] { "attach1", "attach2" });
        var step = new StructureAgentStep(fakeAgent, NullLogger<StructureAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<ConflictDetection.DetectedConflict>(), null);
        var triage = new ConflictTriageResult(new[] { "triage1" });
        var result = await step.ExecuteAsync(input, triage, CancellationToken.None);
        Assert.Equal(2, result.AttachLog.Count);
        Assert.Equal("attach1", result.AttachLog[0]);
        Assert.Equal(input.KnowledgeSystemId, fakeAgent.LastKsId);
        Assert.True(fakeAgent.LastSkipActiveExtractionGate);
    }
}

internal sealed class FakeStructureAgent : IStructureAgent
{
    private readonly IReadOnlyList<string> _log;

    public FakeStructureAgent(IReadOnlyList<string> log) => _log = log;

    public Guid LastKsId { get; private set; }
    public bool LastSkipActiveExtractionGate { get; private set; }

    public Task<IReadOnlyList<string>> AttachIsolatedAsync(
        Guid ksId,
        string? model,
        CancellationToken ct,
        bool skipActiveExtractionGate = false)
    {
        LastKsId = ksId;
        LastSkipActiveExtractionGate = skipActiveExtractionGate;
        return Task.FromResult(_log);
    }
}