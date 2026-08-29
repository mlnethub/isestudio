using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class MergeApplyStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullEditor_ReturnsEmptyAppliedAndAllRemaining()
    {
        var step = new MergeApplyStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(new[]
        {
            new CandidatePair("http://a", "http://b", 0.95),
        });
        var judge = new JudgeResult(new[] { 0 }, null);

        var result = await step.ExecuteAsync(input, candidates, judge, CancellationToken.None);

        Assert.Empty(result.Applied.Pairs);
        Assert.Single(result.Remaining.Conflicts);
    }

    [Fact]
    public async Task ExecuteAsync_NullJudge_EmptyKeptIndices_ReturnsEmpty()
    {
        var step = new MergeApplyStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(Array.Empty<CandidatePair>());
        var judge = new JudgeResult(Array.Empty<int>(), null);

        var result = await step.ExecuteAsync(input, candidates, judge, CancellationToken.None);

        Assert.Empty(result.Applied.Pairs);
        Assert.Empty(result.Remaining.Conflicts);
    }
}
