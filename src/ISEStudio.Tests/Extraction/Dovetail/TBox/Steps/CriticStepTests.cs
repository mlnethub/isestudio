using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class CriticStepTests
{
    private static TBoxVerifyService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    [Fact]
    public async Task ExecuteAsync_EmptyDelta_ReturnsUnchanged()
    {
        var step = new CriticStep(MakeService());
        var input = new TBoxChunkInput(
            ChunkId: 1, Text: "x",
            Delta: TBoxDelta.Empty, Chat: new TestChatClient("{}"));

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.VerifiedDelta.Classes);
        Assert.Empty(output.CriticRejections);
        Assert.Empty(output.AcceptedNorms);
    }
}
