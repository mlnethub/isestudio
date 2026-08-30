using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Tests.Extraction;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

public class AdjudicatorStepTests
{
    private static TBoxVerifyService MakeService() =>
        new(Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));

    [Fact]
    public async Task ExecuteAsync_NoDisputed_ReturnsSuccessNoRecoveredNoFallback()
    {
        var critic = new CriticOutput(
            VerifiedDelta: TBoxDelta.Empty,
            AcceptedNorms: new HashSet<string>(),
            CriticRejections: Array.Empty<RejectedClass>(),
            CriticState: TBoxVerifyResult.Unchanged(TBoxDelta.Empty));
        var chunk = new TBoxChunkInput(1, "x", TBoxDelta.Empty, new TestChatClient("{}"));
        var step = new AdjudicatorStep(MakeService(), NullLogger<AdjudicatorStep>.Instance);

        var output = await step.ExecuteAsync(chunk, critic, CancellationToken.None);

        Assert.True(output.Succeeded);
        Assert.Empty(output.Recovered);
        Assert.Null(output.DenotationFallback);
    }

    [Fact]
    public async Task ExecuteAsync_OperationalFailure_LogsWarning_WithDisputedCountAndExceptionType()
    {
        // AdjudicatorStep's self-fail-soft catch must emit a LogWarning
        // with the disputed-class count and exception full name so
        // dashboards can correlate with the surrounding pipeline stage.
        // The subsequent denotation fallback must still succeed (the
        // chat returns "{}" on the denotation call) so the test isolates
        // the log shape from any further failure modes.
        var chat = new StatefulThrowingAdjudicatorChatClient(
            throwOnAdjudicator: () => new InvalidOperationException("adjudicator LLM call failed (test stub)"));
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<AdjudicatorStep>();

        var step = new AdjudicatorStep(MakeService(), logger);

        var chunk = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        var critic = new CriticOutput(
            // Empty AcceptedNorms forces the lone "vehicle" candidate into
            // `disputed`, so the adjudicator LLM call fires.
            VerifiedDelta: chunk.Delta,
            AcceptedNorms: new HashSet<string>(StringComparer.Ordinal),
            CriticRejections: new[]
            {
                new RejectedClass("vehicle", "no role evidence"),
            },
            CriticState: TBoxVerifyResult.Unchanged(chunk.Delta));

        var output = await step.ExecuteAsync(chunk, critic, CancellationToken.None);

        // Denotation fallback ran (chat returned "{}" on call #3).
        Assert.False(output.Succeeded);
        Assert.NotNull(output.DenotationFallback);

        // Log shape: LogWarning fires exactly once with the expected
        // fields. Capture was wired via LlmCallDiagnosticsTestHelpers
        // — same helper TBoxVerifyServiceTests uses, so the Warning
        // contract is uniform across both.
        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning!;
        Assert.Contains("AdjudicatorStep failed fail-soft (operational failure)", entry.Formatted);
        Assert.Contains("disputedClassCount=1", entry.Formatted);
        Assert.Contains("exceptionType=System.InvalidOperationException", entry.Formatted);
        Assert.Contains("cancellationRequested=False", entry.Formatted);
    }

    [Fact]
    public async Task ExecuteAsync_SdkTimeoutOCE_LogsInformation_NotWarning()
    {
        // The other half of the dd6b418 hygiene rule applied at the
        // step level: when an SDK-timeout OCE bubbles up with the
        // caller's CancellationToken still unset, the fail-soft
        // fallback is an EXPECTED operational path — LlmCallDiagnostics
        // already fired a LogWarning with the precise operationName /
        // elapsedSeconds / configuredTimeoutSec / isCallerCancelled
        // shape, so the step-level log should be LogInformation to
        // avoid doubling the alert noise. Pin the level + message so a
        // future refactor that reverts to a single LogWarning breaks
        // here.
        var chat = new StatefulThrowingAdjudicatorChatClient(
            throwOnAdjudicator: () => new TaskCanceledException("simulated SDK timeout on adjudicator"));
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<AdjudicatorStep>();

        var step = new AdjudicatorStep(MakeService(), logger);

        var chunk = new TBoxChunkInput(
            ChunkId: 1,
            Text: "vehicle",
            Delta: new TBoxDelta(
                Classes: new[] { new ClassMutation("vehicle", Comment: null) },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Chat: chat);

        var critic = new CriticOutput(
            VerifiedDelta: chunk.Delta,
            AcceptedNorms: new HashSet<string>(StringComparer.Ordinal),
            CriticRejections: new[]
            {
                new RejectedClass("vehicle", "no role evidence"),
            },
            CriticState: TBoxVerifyResult.Unchanged(chunk.Delta));

        // CancellationToken.None ⇒ not cancelled, so the OCE matches the
        // `when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)`
        // filter — the catch fires.
        var output = await step.ExecuteAsync(chunk, critic, CancellationToken.None);

        Assert.False(output.Succeeded);
        Assert.NotNull(output.DenotationFallback);

        Assert.Null(logger.SingleWarning);
        Assert.NotNull(logger.SingleInformation);
        var entry = logger.SingleInformation!;
        Assert.Contains("AdjudicatorStep failed fail-soft (SDK timeout)", entry.Formatted);
        Assert.Contains("disputedClassCount=1", entry.Formatted);
        Assert.Contains("exceptionType=System.Threading.Tasks.TaskCanceledException", entry.Formatted);
        Assert.Contains("cancellationRequested=False", entry.Formatted);
    }

    /// <summary>
    /// Chat client that throws the supplied exception on its adjudicator
    /// call (the 1st LLM round inside <c>RunAdjudicatorAsync</c>) and
    /// returns <c>"{}"</c> on every other invocation. Used by both
    /// log-shape tests — the operational-failure path throws a real
    /// exception type, the SDK-timeout path throws
    /// <see cref="TaskCanceledException"/>.
    /// </summary>
    private sealed class StatefulThrowingAdjudicatorChatClient(Func<Exception> throwOnAdjudicator) : IChatClient
    {
        private int _adjudicatorCalls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            // RunAdjudicatorAsync calls GetResponseAsync exactly once
            // inside the try block — throw on call #1 of this client,
            // succeed on every other call (denotation fallback).
            _adjudicatorCalls++;
            if (_adjudicatorCalls == 1)
            {
                throw throwOnAdjudicator();
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