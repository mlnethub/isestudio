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

    [Fact]
    public async Task ExtractAsync_routes_non_OCE_exception_through_LogFailure_and_rethrows()
    {
        // Sibling of ExtractAsync_routes_TaskCanceledException_…: a non-OCE
        // exception (here InvalidOperationException, the stand-in for
        // 401 / 503 / retry-exhausted ClientResultException) must route
        // through LlmCallDiagnostics.LogFailure (NOT LogCancellation),
        // emit a single warning with the "failed after" phrasing, and
        // rethrow so the orchestrator sees a hard failure.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxExtractionService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new InvalidOperationException("simulated upstream 503"));

        var sut = new TBoxExtractionService(Options.Create(DefaultOptions()), logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.ExtractAsync(chat, SampleKs(), SampleChunk(), CancellationToken.None));

        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        // Same operationName as the OCE sibling (ExtractAsync fires a single
        // warning per LLM call regardless of which way it failed) — but the
        // rendered body now uses "failed after" instead of "cancelled after"
        // so log-routing rules can branch on the two streams.
        Assert.Contains("LLM Llm.Extract failed after", entry.Formatted);
        Assert.DoesNotContain("cancelled after", entry.Formatted);
        Assert.Contains("exceptionType=System.InvalidOperationException", entry.Formatted);
        Assert.Contains("message=simulated upstream 503", entry.Formatted);
        Assert.True(entry.ElapsedSeconds >= 0.25,
            $"Stopwatch should capture the injected delay, got {entry.ElapsedSeconds:F2}s");
        Assert.Equal("simulated upstream 503", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_routes_HttpRequestException_through_LogFailure_and_returns_empty_delta()
    {
        // The HttpRequestException catch is fail-soft by design (transient
        // network errors should not abort the whole job — the orchestrator
        // progresses via its own channel). The new LogFailure call inside
        // that catch gives operators visibility WITHOUT changing the
        // fail-soft return contract.
        var logger = new LlmCallDiagnosticsTestHelpers.CapturingLogger<TBoxExtractionService>();
        var chat = new LlmCallDiagnosticsTestHelpers.ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(150),
            exceptionFactory: () => new HttpRequestException("transient network blip"));

        var sut = new TBoxExtractionService(Options.Create(DefaultOptions()), logger);

        var delta = await sut.ExtractAsync(chat, SampleKs(), SampleChunk(), CancellationToken.None);

        Assert.True(delta.IsEmpty,
            "HttpRequestException must still be fail-soft (return empty delta).");
        Assert.NotNull(logger.SingleWarning);
        var entry = logger.SingleWarning;
        Assert.Contains("LLM Llm.Extract failed after", entry.Formatted);
        Assert.Contains("exceptionType=System.Net.Http.HttpRequestException", entry.Formatted);
        Assert.Contains("message=transient network blip", entry.Formatted);
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