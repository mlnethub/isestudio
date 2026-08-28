using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class DenotationStepTests
{
    private static TBoxVerifyService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    [Fact]
    public async Task ExecuteAsync_EmptyClasses_ReturnsUnchanged()
    {
        var critic = new CriticOutput(
            VerifiedDelta: TBoxDelta.Empty,
            AcceptedNorms: new HashSet<string>(),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var adjudicator = new AdjudicatorOutput(
            Succeeded: true,
            Recovered: Array.Empty<ISEStudio.Ontology.ClassMutation>(),
            DenotationFallback: null);
        var chunk = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));
        var step = new DenotationStep(MakeService());

        var output = await step.ExecuteAsync(chunk, critic, adjudicator, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.VerifiedDelta.Classes);
    }
}