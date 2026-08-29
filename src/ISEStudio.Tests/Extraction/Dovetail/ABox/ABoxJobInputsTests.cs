using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxJobInputsTests
{
    [Fact]
    public void CandidateList_EmptyConstruction_HasEmptyPairs()
    {
        var list = new CandidateList(Array.Empty<CandidatePair>());
        Assert.Empty(list.Pairs);
    }

    [Fact]
    public void JudgeResult_EmptyKeptIndices_HasNullReason()
    {
        var result = new JudgeResult(Array.Empty<int>(), null);
        Assert.Empty(result.KeptIndices);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void AppliedMerges_EmptyConstruction_HasEmptyPairs()
    {
        var merges = new AppliedMerges(Array.Empty<MergedClassPair>());
        Assert.Empty(merges.Pairs);
    }

    [Fact]
    public void RemainingConflicts_EmptyConstruction_HasEmptyConflicts()
    {
        var conflicts = new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>());
        Assert.Empty(conflicts.Conflicts);
    }

    [Fact]
    public void CascadeResult_EmptyConstruction_HasEmptyIndividuals()
    {
        var cascade = new CascadeResult(Array.Empty<Guid>());
        Assert.Empty(cascade.UpdatedIndividuals);
    }

    [Fact]
    public void ABoxJobResult_AllSubresultsRoundTrip()
    {
        var applied = new AppliedMerges(new[] { new MergedClassPair("a", "b", 0.95) });
        var remaining = new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>());
        var cascade = new CascadeResult(new[] { Guid.NewGuid() });
        var result = new ABoxJobResult(applied, remaining, cascade);
        Assert.Same(applied, result.Applied);
        Assert.Same(remaining, result.Remaining);
        Assert.Same(cascade, result.Cascade);
    }
}