using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class CandidateGatherStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_ReturnsEmptyCandidateList()
    {
        var step = new CandidateGatherStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,  // not used when judge is null
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);

        var result = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.Empty(result.Pairs);
    }

    [Fact]
    public async Task ExecuteAsync_NullJudge_DoesNotTouchStore()
    {
        // null Store must NOT throw when judge is null (the no-op path).
        var step = new CandidateGatherStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);

        var result = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(result);
    }
}
