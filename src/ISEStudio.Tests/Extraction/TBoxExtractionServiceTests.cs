using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Integration coverage for <see cref="TBoxExtractionService.ExtractAsync"/>'s
/// cancellation diagnostic. The format-level assertions live in
/// <c>LlmCallDiagnosticsTests</c>; these two tests pin the wiring — that
/// the helper actually fires when the chat client raises an OCE, and
/// that it stays silent on the success path.
/// </summary>
public sealed class TBoxExtractionServiceTests
{
    private static ISEStudioOptions DefaultOptions() => new()
    {
        LlmNetworkTimeoutSeconds = 180,
    };

    private static KsContext SampleKs() => new(
        GraphIri: "http://example.org/graph",
        BaseIri: "http://example.org/base#");

    private static ChunkSpan SampleChunk() =>
        new(Idx: 0, Text: "Pump is a device that moves fluid.", CharStart: 0, CharEnd: 39, TokenEstimate: 9);

    [Fact]
    public async Task ExtractAsync_routes_TaskCanceledException_through_LlmCallDiagnostics_and_rethrows()
    {
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxExtractionService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new TaskCanceledException("simulated SDK timeout"));

        var sut = new TBoxExtractionService(Options.Create(DefaultOptions()), logger);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await sut.ExtractAsync(chat, SampleKs(), SampleChunk(), CancellationToken.None));

        // Helper emitted exactly one warning (ExtractAsync fires one OCE
        // path) and rethrew the same exception type, not a wrapper.
        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        Assert.Contains("LLM Llm.Extract cancelled after", entry.Formatted);
        Assert.True(entry.ElapsedSeconds >= 0.25,
            $"Stopwatch should capture the injected delay, got {entry.ElapsedSeconds:F2}s");
        Assert.Equal("simulated SDK timeout", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_on_success_does_not_log_warning()
    {
        // A happy-path chat client that returns a parseable empty delta
        // must not trip the cancellation diagnostic. The point of the
        // helper is to be silent on the success path.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxExtractionService>();
        var chat = new StubChatClient("{\"classes\":[]}");

        var sut = new TBoxExtractionService(Options.Create(DefaultOptions()), logger);

        var delta = await sut.ExtractAsync(chat, SampleKs(), SampleChunk(), CancellationToken.None);

        // ParseTBox always returns a freshly-constructed record (uses
        // List<T> internally), so the success-path delta is never the
        // TBoxDelta.Empty singleton — only the IsEmpty shape matters.
        Assert.True(delta.IsEmpty);
        Assert.Null(logger.SingleWarning);
        Assert.Equal(0, logger.Count);
    }

    /// <summary>
    /// Tiny <see cref="IChatClient"/> that echoes a fixed assistant reply.
    /// Only used by the success-path test — the cancellation tests use
    /// <see cref="LlmCallDiagnosticsTestHelpers.ThrowingChatClient"/> instead.
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        private readonly string _reply;
        public StubChatClient(string reply) => _reply = reply;
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