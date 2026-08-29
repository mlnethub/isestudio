using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxJobPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyInputs_ReturnsEmptyResult()
    {
        var pipeline = new ABoxJobPipeline(
            gather: new CandidateGatherStep(null),
            embed: new EmbeddingMatchStep(null),
            judge: new LLMJudgeStep(null),
            merge: new MergeApplyStep(null, audit: null),
            cascade: new CascadeRetypeStep(null, audit: null),
            final: new FinalMergeStep());

        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: new NullChat(),
            Embedder: null!,
            MinConfidence: 0.90);

        var output = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.Applied.Pairs);
        Assert.Empty(output.Remaining.Conflicts);
        Assert.Empty(output.Cascade.UpdatedIndividuals);
    }

    private sealed class NullChat : IChatClient
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
