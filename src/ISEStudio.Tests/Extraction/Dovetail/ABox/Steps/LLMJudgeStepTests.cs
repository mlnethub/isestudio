using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class LLMJudgeStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_KeepsAllCandidates()
    {
        var step = new LLMJudgeStep(null);
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
            new CandidatePair("http://a", "http://b", 0.9),
            new CandidatePair("http://c", "http://d", 0.7),
        });

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        Assert.Equal(2, result.KeptIndices.Count);
        Assert.Equal("judge_unavailable", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCandidates_ReturnsEmptyKeptIndices()
    {
        var step = new LLMJudgeStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(Array.Empty<CandidatePair>());

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        Assert.Empty(result.KeptIndices);
        Assert.Null(result.Reason);
    }
}
