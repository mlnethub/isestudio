using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Integration coverage for <see cref="TBoxVerifyService.CallAsync"/>'s
/// cancellation diagnostic. <c>CallAsync</c> is private, but the
/// production wiring that fires it is
/// <c>RunDenotationAsync → VerifyClassDenotationsAsync → CallAsync</c> —
/// which <see cref="DenotationStep"/> invokes directly. Driving the step
/// end-to-end is the closest public surface that reaches
/// <c>CallAsync("Denotation", …)</c>, and it matches the exact path that
/// tripped production job #5d6672bb.
///
/// <para>Format-level assertions live in <c>LlmCallDiagnosticsTests</c>;
/// the two tests here pin the wiring — that the helper actually fires
/// when the chat client raises an OCE on the denotation call, and that
/// it stays silent on the success path.</para>
/// </summary>
public sealed class TBoxVerifyServiceTests
{
    private static ISEStudioOptions DefaultOptions() => new()
    {
        LlmNetworkTimeoutSeconds = 180,
        AutoApplyFloor = 0.85,
    };

    [Fact]
    public async Task CriticStep_routes_TaskCanceledException_through_LlmCallDiagnostics_with_stage_Critic()
    {
        // Step 1 of TBoxChunkPipeline (Dovetail) — invokes
        // TBoxVerifyService.RunCriticAsync which dispatches to CallAsync
        // with stage="Critic". A chat client that throws TaskCanceledException
        // exercises the exact production path (Critic is the FIRST stage in
        // the chunk pipeline; an OCE here discards the chunk). Without this
        // test, a future refactor that drops the per-stage operationName
        // ("Llm.TBoxVerify.Critic") would still pass the existing
        // DenotationStep test and leave the Critic branch invisible in
        // production logs.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxVerifyService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new TaskCanceledException("simulated critic timeout"));

        var verify = new TBoxVerifyService(Options.Create(DefaultOptions()), logger);
        var step = new CriticStep(verify);

        // RunCriticAsync short-circuits with `return Unchanged(delta)` when
        // both delta.Classes and delta.Axioms (subclass rows) are empty —
        // so the diagnostic would never fire on an empty delta. Mirror
        // DenotationStep's setup with a single non-empty class so the
        // helper is actually reached.
        var input = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await step.ExecuteAsync(input, CancellationToken.None));

        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        // Pin the Critic operationName — that's the whole point of routing
        // every stage through the same helper with a stage parameter.
        Assert.Contains("LLM Llm.TBoxVerify.Critic cancelled after", entry.Formatted);
        Assert.Contains("isCallerCancelled=False", entry.Formatted);
        Assert.True(entry.ElapsedSeconds >= 0.25,
            $"Stopwatch should capture the injected delay, got {entry.ElapsedSeconds:F2}s");
    }

    [Fact]
    public async Task CriticStep_on_success_does_not_log_warning()
    {
        // Happy-path critic: chat returns a parseable verdict, no OCE,
        // no warning. Helper stays silent. Same hygiene as the
        // DenotationStep success test.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxVerifyService>();
        // Critic's reply shape is {class_decisions, subclass_decisions};
        // both empty is the simplest valid payload that lets ApplyTBoxRoleDecisions
        // return Unchanged(delta) without throwing.
        var chat = new StubVerifyChatClient(
            "{\"class_decisions\":[],\"subclass_decisions\":[]}");

        var verify = new TBoxVerifyService(Options.Create(DefaultOptions()), logger);
        var step = new CriticStep(verify);

        var input = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        var output = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Null(logger.SingleWarning);
        Assert.Equal(0, logger.Count);
    }

    [Fact]
    public async Task DenotationStep_routes_TaskCanceledException_through_LlmCallDiagnostics_with_stage_Denotation()
    {
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxVerifyService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new TaskCanceledException("simulated verify timeout"));

        var verify = new TBoxVerifyService(Options.Create(DefaultOptions()), logger);
        var step = new DenotationStep(verify);

        // Non-empty delta is required: VerifyClassDenotationsAsync
        // short-circuits with `return state` when candidateClasses is
        // empty, so an empty delta would never reach CallAsync and the
        // diagnostic would never fire — masking the wiring we want to
        // test.
        var input = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        var critic = new CriticOutput(
            VerifiedDelta: input.Delta,
            AcceptedNorms: new HashSet<string>(StringComparer.Ordinal),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(input.Delta));

        var adjudicator = new AdjudicatorOutput(
            Succeeded: true,
            Recovered: input.Delta.Classes,
            DenotationFallback: null);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await step.ExecuteAsync(input, critic, adjudicator, CancellationToken.None));

        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        // Operation name carries the stage — that's the whole point of
        // routing both services through the same helper with a per-stage
        // operationName. Pin the exact phrase so a future rename breaks
        // here, not in the server log.
        Assert.Contains("LLM Llm.TBoxVerify.Denotation cancelled after", entry.Formatted);
        Assert.True(entry.ElapsedSeconds >= 0.25,
            $"Stopwatch should capture the injected delay, got {entry.ElapsedSeconds:F2}s");
    }

    [Fact]
    public async Task DenotationStep_on_success_does_not_log_warning()
    {
        // Happy-path denotation: chat returns a parseable verdict, no
        // OCE, no warning. The helper stays silent.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxVerifyService>();
        var chat = new StubVerifyChatClient("{\"class_decisions\":[]}");

        var verify = new TBoxVerifyService(Options.Create(DefaultOptions()), logger);
        var step = new DenotationStep(verify);

        var input = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        var critic = new CriticOutput(
            VerifiedDelta: input.Delta,
            AcceptedNorms: new HashSet<string>(StringComparer.Ordinal),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(input.Delta));

        var adjudicator = new AdjudicatorOutput(
            Succeeded: true,
            Recovered: input.Delta.Classes,
            DenotationFallback: null);

        var output = await step.ExecuteAsync(input, critic, adjudicator, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Null(logger.SingleWarning);
        Assert.Equal(0, logger.Count);
    }

    /// <summary>
    /// Echoes a fixed assistant reply. Only used by the success-path
    /// test — the cancellation test uses
    /// <see cref="LlmCallDiagnosticsTestHelpers.ThrowingChatClient"/>.
    /// </summary>
    private sealed class StubVerifyChatClient : IChatClient
    {
        private readonly string _reply;
        public StubVerifyChatClient(string reply) => _reply = reply;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}