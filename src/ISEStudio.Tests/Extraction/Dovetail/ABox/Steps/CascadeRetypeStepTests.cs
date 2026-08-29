using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class CascadeRetypeStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullEditor_ReturnsEmptyCascade()
    {
        var step = new CascadeRetypeStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var mergeOutput = new MergeApplyOutput(
            Applied: new AppliedMerges(Array.Empty<MergedClassPair>()),
            Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));

        var result = await step.ExecuteAsync(input, mergeOutput, CancellationToken.None);

        Assert.Empty(result.UpdatedIndividuals);
    }

    [Fact]
    public async Task ExecuteAsync_NoAppliedMerges_ReturnsEmptyCascade()
    {
        var step = new CascadeRetypeStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var mergeOutput = new MergeApplyOutput(
            Applied: new AppliedMerges(new[] { new MergedClassPair("a", "b", 0.95) }),
            Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));

        // null editor means cascade is a no-op
        var result = await step.ExecuteAsync(input, mergeOutput, CancellationToken.None);

        Assert.Empty(result.UpdatedIndividuals);
    }
}
