using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Test doubles shared by <see cref="TBoxExtractionServiceTests"/>,
/// <see cref="TBoxVerifyServiceTests"/>, and the
/// <see cref="LlmCallDiagnosticsTests"/> helper-level tests. Lives in its
/// own file so the three suites can pull the same fixture without each
/// redefining its own <c>ThrowingChatClient</c> /
/// <c>CapturingLogger&lt;T&gt;</c>.
/// </summary>
internal static class LlmCallDiagnosticsTestHelpers
{
    /// <summary>
    /// Chat client that delays for <paramref name="delay"/> then throws the
    /// exception produced by <paramref name="exceptionFactory"/>. Mirrors the
    /// real OpenAI SDK's behaviour where the request is allowed to run
    /// before its internal CTS triggers the cancel — gives the production
    /// Stopwatch a non-zero elapsed value to capture.
    /// </summary>
    internal sealed class ThrowingChatClient : IChatClient
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
    /// formatted message + its <c>ElapsedSeconds</c> scalar. Production
    /// services log at most one warning per LLM call, so capturing more
    /// than one throws — surfaces silent double-log regressions.
    /// Also captures a single information-level entry (overwritten on
    /// subsequent Information calls) so fail-soft / SDK-timeout
    /// <see cref="LogLevel.Information"/> paths can be asserted on too —
    /// see <c>AdjudicatorStep_operational_failure_logs_warning</c>.
    /// </summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public LogEntry? SingleWarning { get; private set; }
        public LogEntry? SingleInformation { get; private set; }
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
            // The diagnostic template is
            //   "LLM {OperationName} cancelled after {ElapsedSeconds:F2}s ..."
            // Pull the {ElapsedSeconds:F2} scalar so the test can assert
            // "elapsed ≥ 0.25 s" without parsing the formatted string.
            var formatted = formatter(state, exception);
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

            if (logLevel == LogLevel.Warning)
            {
                Count++;
                if (SingleWarning is not null)
                {
                    throw new InvalidOperationException(
                        $"CapturingLogger expected at most one Warning entry; saw {Count}.");
                }
                SingleWarning = new LogEntry(formatted, elapsed);
            }
            else if (logLevel == LogLevel.Information)
            {
                // Allow overwrite — multiple Information entries are legal
                // (e.g. retry-loop traces); tests assert on the LAST one.
                SingleInformation = new LogEntry(formatted, elapsed);
            }
            // Debug/Trace/Error/Critical: ignored.
        }

        public sealed record LogEntry(string Formatted, double ElapsedSeconds);
    }
}