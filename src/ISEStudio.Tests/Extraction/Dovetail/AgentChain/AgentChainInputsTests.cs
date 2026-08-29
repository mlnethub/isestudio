using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class AgentChainInputsTests
{
    [Fact]
    public void ConflictTriageResult_EmptyConstruction_HasEmptyTriageLog()
    {
        var result = new ConflictTriageResult(Array.Empty<string>());
        Assert.Empty(result.TriageLog);
    }

    [Fact]
    public void StructureAttachResult_EmptyConstruction_HasEmptyAttachLog()
    {
        var result = new StructureAttachResult(Array.Empty<string>());
        Assert.Empty(result.AttachLog);
    }

    [Fact]
    public void AgentChainInput_EmptyConstruction_HasEmptyConflictsAndNullModel()
    {
        var input = new AgentChainInput(
            JobId: Guid.Empty,
            KnowledgeSystemId: Guid.Empty,
            Conflicts: Array.Empty<ConflictDetection.DetectedConflict>(),
            Model: null);
        Assert.Equal(Guid.Empty, input.JobId);
        Assert.Empty(input.Conflicts);
        Assert.Null(input.Model);
    }

    [Fact]
    public void AgentChainResult_AllSubresultsRoundTrip()
    {
        var triage = new ConflictTriageResult(new[] { "log1", "log2" });
        var structure = new StructureAttachResult(new[] { "log3" });
        var result = new AgentChainResult(triage, structure);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
        Assert.Equal(2, result.Triage.TriageLog.Count);
        Assert.Single(result.Structure.AttachLog);
    }
}