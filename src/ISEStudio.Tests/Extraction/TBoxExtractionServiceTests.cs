using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Unit tests for <see cref="TBoxExtractionService.ExtractAsync"/>. The
/// service's <c>OperationCanceledException</c> path is the production
/// tripwire for "extraction job marked Cancelled with no user-cancellation
/// intent" — the only way to tell whether the SDK hit its internal
/// <c>NetworkTimeout</c> (and the orchestrator should retry with a longer
/// timeout) versus a genuinely-cancelled request is to capture elapsed
/// seconds next to the exception type. These tests pin the diagnostic.
/// </summary>
public sealed class TBoxExtractionServiceTests
{
    private const string GraphIri = "http://goodcrew.local/ks/test/tbox-extract";
    private const string BaseIri = GraphIri + "/onto#";

    [Fact]
    public async Task ExtractAsync_on_TaskCanceledException_logs_elapsed_and_rethrows()
    {
        // Chat client that delays 250 ms then throws TaskCanceledException
        // (mirrors what System.ClientModel does when its internal CTS
        // triggers). The OCE must propagate AND the logger must capture
        // elapsed ≥ 0.25 s and the configured timeout — pairing those two
        // tells us whether to bump LlmNetworkTimeoutSeconds on the next
        // configuration change.
        var chat = new ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(250),
            exceptionFactory: () => new TaskCanceledException("simulated SDK timeout"));
        var capturing = new CapturingLogger<TBoxExtractionService>();
        var sut = new TBoxExtractionService(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 180 }),
            capturing);

        var ks = new KsContext(GraphIri, BaseIri, Name: "Test KS");
        var chunk = new ChunkSpan(0, "the quick brown fox", CharStart: 0, CharEnd: 19, TokenEstimate: 4);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            sut.ExtractAsync(chat, ks, chunk, CancellationToken.None));

        Assert.NotNull(capturing.SingleWarning);
        var entry = capturing.SingleWarning!;
        Assert.True(entry.ElapsedSeconds >= 0.25,
            $"expected elapsed ≥ 0.25s, got {entry.ElapsedSeconds:F3}s");
        Assert.Contains("TaskCanceledException", entry.Formatted);
        Assert.Contains("configuredTimeoutSec=180", entry.Formatted);
        Assert.Contains("callerTokenCancelled=False", entry.Formatted);
        // The exception we threw is the outer one; no inner exception
        // was attached, so the diagnostic must say so explicitly rather
        // than emitting "null" (server log readers grep for "<none>").
        Assert.Contains("innerType=<none>", entry.Formatted);
    }

    [Fact]
    public async Task ExtractAsync_on_TaskCanceledException_with_inner_logs_inner_type()
    {
        // Some SDK paths wrap the timeout-cancelled OCE inside an outer
        // OCE (e.g. when the WithLlmActivity scope sees the cancellation
        // before unwinding). The diagnostic must surface the inner type so
        // we can distinguish "user cancelled via ct" (no inner) from "SDK
        // internal timeout" (inner == TaskCanceledException).
        var inner = new TaskCanceledException("inner SDK timeout");
        var chat = new ThrowingChatClient(
            delay: TimeSpan.FromMilliseconds(50),
            exceptionFactory: () => new OperationCanceledException("outer wrapper", inner));
        var capturing = new CapturingLogger<TBoxExtractionService>();
        var sut = new TBoxExtractionService(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 180 }),
            capturing);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExtractAsync(chat, new KsContext(GraphIri, BaseIri), new ChunkSpan(0, "x", CharStart: 0, CharEnd: 1, TokenEstimate: 1), CancellationToken.None));

        Assert.NotNull(capturing.SingleWarning);
        Assert.Contains("innerType=System.Threading.Tasks.TaskCanceledException",
            capturing.SingleWarning!.Formatted);
    }

    [Fact]
    public async Task ExtractAsync_on_user_cancellation_marks_callerTokenCancelled()
    {
        // A pre-cancelled token simulates the orchestrator deciding to
        // abort (HTTP request aborted, host shutdown, etc.). The
        // diagnostic must log callerTokenCancelled=True so we don't
        // confuse this with an SDK timeout — same exception type, very
        // different remediation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var chat = new ThrowingChatClient(
            delay: TimeSpan.Zero,
            exceptionFactory: () => new OperationCanceledException(cts.Token));
        var capturing = new CapturingLogger<TBoxExtractionService>();
        var sut = new TBoxExtractionService(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 180 }),
            capturing);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExtractAsync(chat, new KsContext(GraphIri, BaseIri), new ChunkSpan(0, "x", CharStart: 0, CharEnd: 1, TokenEstimate: 1), cts.Token));

        Assert.NotNull(capturing.SingleWarning);
        Assert.Contains("callerTokenCancelled=True", capturing.SingleWarning!.Formatted);
    }

    [Fact]
    public async Task ExtractAsync_on_success_does_not_log_warning()
    {
        // The Stopwatch + warning block sits inside the OCE catch only —
        // a clean success path must not surface any warning-level log
        // entry. (Defends against a future refactor that accidentally
        // lifts the logger call out of the catch.)
        var chat = new FakeChat().Enqueue(FakeChat.ValidTBoxDelta);
        var capturing = new CapturingLogger<TBoxExtractionService>();
        var sut = new TBoxExtractionService(
            Options.Create(new ISEStudioOptions()),
            capturing);

        var delta = await sut.ExtractAsync(
            chat,
            new KsContext(GraphIri, BaseIri),
            new ChunkSpan(0, "body", CharStart: 0, CharEnd: 4, TokenEstimate: 1),
            CancellationToken.None);

        Assert.NotEmpty(delta.Classes);
        Assert.Null(capturing.SingleWarning);
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    /// <summary>
    /// Chat client that delays for <paramref name="delay"/> then throws the
    /// exception produced by <paramref name="exceptionFactory"/>. Mirrors the
    /// real OpenAI SDK's behaviour where the request is allowed to run
    /// before its internal CTS triggers the cancel.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly TimeSpan _delay;
        private readonly Func<Exception> _exceptionFactory;

        public ThrowingChatClient(TimeSpan delay, Func<Exception> exceptionFactory)
        {
            _delay = delay;
            _exceptionFactory = exceptionFactory;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
            throw _exceptionFactory();
        }

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

    /// <summary>
    /// <see cref="ILogger{T}"/> that captures the first warning-level
    /// formatted message + its elapsed-seconds scalar property. We only
    /// need one entry per test (the production service logs at most one
    /// warning per chunk's LLM call), so <see cref="SingleWarning"/>
    /// throws if more than one is captured — that keeps a regression
    /// from silently emitting multiple diagnostics visible.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public LogEntry? SingleWarning { get; private set; }
        public int Count { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Count++;
            if (SingleWarning is not null)
            {
                throw new InvalidOperationException(
                    $"CapturingLogger expected at most one Warning entry; saw {Count}.");
            }

            var formatted = formatter(state, exception);
            // The diagnostic template is
            //   "LLM Extract cancelled after {ElapsedSeconds:F2}s ..."
            // Pull the {ElapsedSeconds:F2} scalar so the test can assert
            // "elapsed ≥ 0.25 s" without parsing the formatted string.
            var elapsed = 0.0;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
            {
                foreach (var kv in kvs)
                {
                    if (kv.Key == "ElapsedSeconds" && kv.Value is double d)
                    {
                        elapsed = d;
                        break;
                    }
                }
            }
            SingleWarning = new LogEntry(formatted, elapsed);
        }

        public sealed record LogEntry(string Formatted, double ElapsedSeconds);
    }
}