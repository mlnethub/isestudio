using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.AI;
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

    [Fact]
    public async Task ExecuteAsync_AdjudicatorThrows_ReturnsDenotationFallback()
    {
        // Final-review F-4: integration test that exercises the
        // AdjudicatorStep self-fail-soft catch → ChunkMergeStep
        // DenotationFallback short-circuit through the Dovetail-generated
        // TBoxChunkPipeline wiring. AdjudicatorStep is sealed, so the test
        // triggers the fail-soft path indirectly by making the chat client
        // throw on its adjudicator call. The catch block then runs
        // RunDenotationAsync, populates AdjudicatorOutput.DenotationFallback,
        // and ChunkMergeStep returns that fallback.
        //
        // The chat client's call sequence is observable (call #1=critic,
        // #2=adjudicator[throws], #3=denotation-in-catch, #4=denotation-step).
        // Asserting CallCount == 4 distinguishes this path from the normal
        // happy path where only 3 chat calls happen (critic + adjudicator +
        // denotation, no catch).
        var verify = new TBoxVerifyService(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        var chat = new ThrowingAdjudicatorChatClient();

        var pipeline = new TBoxChunkPipeline(
            new CriticStep(verify),
            new AdjudicatorStep(verify),
            new DenotationStep(verify),
            new ChunkMergeStep());

        // Non-empty delta so AdjudicatorStep actually invokes the
        // adjudicator LLM call (rather than short-circuiting on
        // disputed.Count == 0). The label "vehicle" is lexically grounded
        // in the source text but ApplyTBoxRoleDecisions rejects it because
        // the chat returns an empty class_decisions array.
        var delta = new TBoxDelta(
            Classes: new[] { new ClassMutation("vehicle", Comment: null) },
            ObjectProperties: Array.Empty<PropertyMutation>(),
            DataProperties: Array.Empty<PropertyMutation>(),
            Axioms: Array.Empty<AxiomMutation>());
        var input = new TBoxChunkInput(1, "vehicle", delta, chat);

        var result = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.Equal(4, chat.CallCount);
        Assert.NotNull(result);
        // All classes were rejected (empty class_decisions + no role evidence
        // means every candidate falls into the "missing or ungrounded" reject
        // branch of ApplyTBoxRoleDecisions).
        Assert.Empty(result.Delta.Classes);
    }

    /// <summary>
    /// Stateful chat client that throws an <see cref="InvalidOperationException"/>
    /// on its second invocation (the adjudicator call) and returns an empty
    /// JSON object on every other invocation. Used to exercise the
    /// AdjudicatorStep self-fail-soft catch block.
    /// </summary>
    private sealed class ThrowingAdjudicatorChatClient : IChatClient
    {
        private int _count;

        public int CallCount => _count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            _count++;
            if (_count == 2)
            {
                throw new InvalidOperationException("adjudicator LLM call failed (test stub)");
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}