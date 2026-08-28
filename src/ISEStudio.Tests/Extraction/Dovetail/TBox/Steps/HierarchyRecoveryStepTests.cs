using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class HierarchyRecoveryStepTests
{
    private static TBoxVerifyService MakeVerifyService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    private static HierarchyRecoveryService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }), MakeVerifyService());

    [Fact]
    public async Task ExecuteAsync_PerChunkTextEmpty_AndServiceRegistered_ThrowsArgumentException()
    {
        // Spec §6 D7(a): the orchestrator must populate PerChunkText from
        // ctx.Chunks[i].Text in chunk-index order before invoking
        // TBoxJobPipeline.ExecuteAsync. HierarchyRecoveryStep guards against
        // the silent footgun where an empty PerChunkText yields an empty
        // string passed to HierarchyRecoveryService.RecoverAsync (final
        // review F-6).
        var step = new HierarchyRecoveryStep(MakeService());
        var input = new TBoxJobInput(
            JobId: Guid.NewGuid(),
            ChunkResults: Array.Empty<TBoxVerifyResult>(),
            PerChunkRejections: Array.Empty<CorpusRecoveryChunk>(),
            FinalClassVocabulary: Array.Empty<string>(),
            PerChunkText: Array.Empty<string>(),
            Chat: new TestChatClient("{}"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => step.ExecuteAsync(input, CancellationToken.None));

        Assert.Equal("input", ex.ParamName);
        Assert.Contains("PerChunkText", ex.Message);
        Assert.Contains("orchestrator", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PerChunkTextEmpty_AndServiceNull_ReturnsEmpty()
    {
        // The guard only fires when HierarchyRecoveryService is registered
        // (OptionalSegment replaces the step with a NoOp when the service is
        // absent). Service-null + empty PerChunkText must NOT throw — the
        // step returns its Enabled:false wrapper as designed.
        var step = new HierarchyRecoveryStep(service: null);
        var input = new TBoxJobInput(
            JobId: Guid.NewGuid(),
            ChunkResults: Array.Empty<TBoxVerifyResult>(),
            PerChunkRejections: Array.Empty<CorpusRecoveryChunk>(),
            FinalClassVocabulary: Array.Empty<string>(),
            PerChunkText: Array.Empty<string>(),
            Chat: new TestChatClient("{}"));

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.False(output.Enabled);
        Assert.Same(HierarchyRecoveryResult.Empty, output.Result);
    }
}