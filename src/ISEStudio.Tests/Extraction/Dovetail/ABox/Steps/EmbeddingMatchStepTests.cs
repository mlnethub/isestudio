using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class EmbeddingMatchStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_ReturnsSameCandidateList()
    {
        var step = new EmbeddingMatchStep(null);
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
            new CandidatePair("http://a", "http://b", null),
        });

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        Assert.Same(candidates, result);
    }

    [Fact]
    public async Task ExecuteAsync_NullJudge_EmptyInputReturnsEmpty()
    {
        var step = new EmbeddingMatchStep(null);
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

        Assert.Empty(result.Pairs);
    }
}
