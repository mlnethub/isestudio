using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox;

public class TBoxChunkPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyDelta_ReturnsUnchanged()
    {
        var verify = new TBoxVerifyService(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        var critic = new CriticStep(verify);
        var adjudicator = new AdjudicatorStep(verify);
        var denotation = new DenotationStep(verify);
        var merge = new ChunkMergeStep();

        var pipeline = new TBoxChunkPipeline(critic, adjudicator, denotation, merge);
        var input = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Delta.Classes);
    }
}