using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class ChunkMergeStepTests
{
    [Fact]
    public async Task ExecuteAsync_AllSuccessAndNoRecovered_ReturnsDenotation()
    {
        var chunk = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));
        var critic = new CriticOutput(
            VerifiedDelta: TBoxDelta.Empty,
            AcceptedNorms: new HashSet<string>(),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var adjudicator = new AdjudicatorOutput(
            Succeeded: true,
            Recovered: Array.Empty<ClassMutation>(),
            DenotationFallback: null);
        var denotation = new DenotationOutput(
            VerifiedDelta: TBoxDelta.Empty,
            Rejections: Array.Empty<RejectedClass>(),
            Recoveries: Array.Empty<RecoveredClass>(),
            DenotationState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var input = new MergeInput(chunk, critic, adjudicator, denotation);
        var step = new ChunkMergeStep();

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.Delta.Classes);
    }

    [Fact]
    public async Task ExecuteAsync_DenotationFallbackNonNull_ReturnsFallback()
    {
        var chunk = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));
        var critic = new CriticOutput(
            VerifiedDelta: TBoxDelta.Empty,
            AcceptedNorms: new HashSet<string>(),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var fallback = TBoxVerifyResult.Unchanged(TBoxDelta.Empty);
        var adjudicator = new AdjudicatorOutput(
            Succeeded: false,
            Recovered: Array.Empty<ClassMutation>(),
            DenotationFallback: fallback);
        var denotation = new DenotationOutput(
            VerifiedDelta: TBoxDelta.Empty,
            Rejections: Array.Empty<RejectedClass>(),
            Recoveries: Array.Empty<RecoveredClass>(),
            DenotationState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var input = new MergeInput(chunk, critic, adjudicator, denotation);
        var step = new ChunkMergeStep();

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.Same(fallback, output);
    }
}
