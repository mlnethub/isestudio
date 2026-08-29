using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class AgentChainInputsTests
{
    [Fact]
    public void ConflictTriageResult_EmptyConstruction_HasEmptyTriagedAndZeroAttached()
    {
        var result = new ConflictTriageResult(Array.Empty<ConflictDetection.DetectedConflict>(), 0);
        Assert.Empty(result.TriagedConflicts);
        Assert.Equal(0, result.RecommendationsAttached);
    }

    [Fact]
    public void StructureAttachResult_EmptyConstruction_HasZeroAttachedAndZeroCreated()
    {
        var result = new StructureAttachResult(0, 0);
        Assert.Equal(0, result.IsolatedAttached);
        Assert.Equal(0, result.NewClassesCreated);
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
        var triage = new ConflictTriageResult(Array.Empty<ConflictDetection.DetectedConflict>(), 3);
        var structure = new StructureAttachResult(5, 2);
        var result = new AgentChainResult(triage, structure);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
        Assert.Equal(3, result.Triage.RecommendationsAttached);
        Assert.Equal(5, result.Structure.IsolatedAttached);
    }
}
