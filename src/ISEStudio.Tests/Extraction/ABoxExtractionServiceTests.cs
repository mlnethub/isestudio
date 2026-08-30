using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Integration coverage for <see cref="ABoxExtractionService.ExtractAsync"/>'s
/// cancellation diagnostic. <c>ABoxExtractionService</c> has no dedicated
/// test file before this slice — the ABox side of the extraction pipeline
/// is covered through the orchestrator / Dovetail step tests, which
/// exercise only the success path. The cancellation path needed a
/// targeted test after commit <c>b2f7bfd</c> extracted
/// <see cref="ISEStudio.Observability.LlmCallDiagnostics"/> as the shared
/// helper.
///
/// <para>These two tests pin the wiring — that the helper actually fires
/// when the chat client raises an OCE, and that it stays silent on the
/// success path. Format-level assertions live in
/// <c>LlmCallDiagnosticsTests</c>.</para>
/// </summary>
public sealed class ABoxExtractionServiceTests
{
    private static ISEStudioOptions DefaultOptions() => new()
    {
        LlmNetworkTimeoutSeconds = 180,
    };

    private static KsContext SampleKs() => new(
        GraphIri: "http://example.org/graph",
        BaseIri: "http://example.org/base#");

    private static ChunkSpan SampleChunk() =>
        new(Idx: 0, Text: "Pump-001 is a Pump installed in Plant-A.", CharStart: 0, CharEnd: 41, TokenEstimate: 10);

    [Fact]
    public async Task ExtractAsync_routes_TaskCanceledException_through_LlmCallDiagnostics_with_ABoxExtract_operationName()
    {
        // The ABox extractor uses the operationName "Llm.ABoxExtract"
        // (distinct from TBoxExtractionService's "Llm.Extract") so the
        // server-log dashboard can separate the two pipelines — they have
        // different baselines (ABox fires per-chunk after the TBox
        // phase; TBox fires per-chunk first). Pin the exact string here
        // so a future rename breaks the test, not the dashboard.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<ABoxExtractionService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new TaskCanceledException("simulated ABox SDK timeout"));

        var sut = new ABoxExtractionService(Options.Create(DefaultOptions()), logger);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await sut.ExtractAsync(
                chat, SampleKs(), SampleChunk(),
                existingClassLabels: new[] { "Pump" },
                cancellationToken: CancellationToken.None));

        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        Assert.Contains("LLM Llm.ABoxExtract cancelled after", entry.Formatted);
        Assert.True(entry.ElapsedSeconds >= 0.2,
            $"Stopwatch should capture the injected delay, got {entry.ElapsedSeconds:F2}s");
    }

    [Fact]
    public async Task ExtractAsync_on_success_does_not_log_warning()
    {
        // Happy-path ABox extractor: chat returns a parseable empty delta,
        // no OCE, no warning. The helper stays silent on the success path.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<ABoxExtractionService>();
        var chat = new StubAboxChatClient("{\"individuals\":[]}");

        var sut = new ABoxExtractionService(Options.Create(DefaultOptions()), logger);

        var delta = await sut.ExtractAsync(
            chat, SampleKs(), SampleChunk(),
            existingClassLabels: new[] { "Pump" },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(delta);
        Assert.Null(logger.SingleWarning);
        Assert.Equal(0, logger.Count);
    }

    /// <summary>
    /// Echoes a fixed assistant reply. Only used by the success-path
    /// test — the cancellation test uses
    /// <see cref="LlmCallDiagnosticsTestHelpers.ThrowingChatClient"/>.
    /// </summary>
    private sealed class StubAboxChatClient : IChatClient
    {
        private readonly string _reply;
        public StubAboxChatClient(string reply) => _reply = reply;
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