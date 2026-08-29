using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class FinalMergeStepTests
{
    [Fact]
    public async Task ExecuteAsync_RoundTripsAllThreeSubresults()
    {
        var step = new FinalMergeStep();
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
        var cascade = new CascadeResult(new[] { Guid.NewGuid() });

        var result = await step.ExecuteAsync(input, mergeOutput, cascade, CancellationToken.None);

        Assert.Same(mergeOutput.Applied, result.Applied);
        Assert.Same(mergeOutput.Remaining, result.Remaining);
        Assert.Same(cascade, result.Cascade);
    }
}
