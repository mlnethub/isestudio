using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox;

public class TBoxJobPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyInputs_ReturnsEmptyResult()
    {
        // Local stand-ins: Task 8 wires the real OptionalSegment-based
        // registration against IRunWithExtractionGuard. For Slice 1 we exercise
        // the pipeline contract with concrete step instances whose service
        // dependency is null — each step returns its Enabled:false wrapper.
        var pipeline = new TBoxJobPipeline(
            chunk: new ChunkPipelineStep(),
            corpus: new CorpusRecoveryStep(service: null),
            hierarchy: new HierarchyRecoveryStep(service: null),
            merge: new JobMergeStep());
        var input = new TBoxJobInput(
            JobId: Guid.NewGuid(),
            ChunkResults: Array.Empty<TBoxVerifyResult>(),
            PerChunkRejections: Array.Empty<CorpusRecoveryChunk>(),
            FinalClassVocabulary: Array.Empty<string>(),
            PerChunkText: Array.Empty<string>(),
            Chat: new TestJobChatClient());

        var output = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.ChunkResults);
        Assert.Empty(output.Corpus.Classes);
        Assert.Empty(output.Hierarchy.Edges);
        Assert.Empty(output.Hierarchy.Classes);
    }

    private sealed class TestJobChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
