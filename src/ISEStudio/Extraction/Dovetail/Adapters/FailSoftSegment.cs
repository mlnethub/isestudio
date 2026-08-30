using Dovetail;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps <paramref name="inner"/> and converts exceptions into a fallback
/// result. Operational failures (anything other than
/// <see cref="OperationCanceledException"/> when cancellation is requested)
/// are logged and routed to <paramref name="fallbackFactory"/>.
/// Strictly aligned with Python fail-soft semantics.
///
/// <para>Log shape is split into two levels: SDK-timeout OCEs (OCE with
/// <see cref="CancellationToken.IsCancellationRequested"/> false — the
/// System.ClientModel <c>NetworkTimeout</c> fingerprint) emit
/// <see cref="LogLevel.Information"/> because the inner chat-client call
/// already fired a <see cref="LogLevel.Warning"/> via
/// <c>LlmCallDiagnostics.LogCancellation</c> with the precise
/// <c>operationName / elapsedSeconds / configuredTimeoutSec /
/// isCallerCancelled</c> shape; this line just notes the fail-soft
/// fallback is engaged. Operational failures (JsonException, network
/// errors, unhandled bugs) emit <see cref="LogLevel.Warning"/> with the
/// exception as the <c>Exception</c> payload so dashboards keep paging.</para>
/// </summary>
public sealed class FailSoftSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    Func<TIn, TOut> fallbackFactory,
    ILogger<FailSoftSegment<TIn, TOut>> logger) : IPipelineSegment<TIn, TOut>
{
    private readonly IPipelineSegment<TIn, TOut> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<TIn, TOut> _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
    private readonly ILogger<FailSoftSegment<TIn, TOut>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Two-level logging: SDK-timeout OCEs (cancellation not
            // requested, but the caller's CancellationToken bubbled an OCE
            // — the System.ClientModel NetworkTimeout fingerprint) are
            // LogInformation because LlmCallDiagnostics already fired a
            // LogWarning inside the chat-client call with the precise
            // operationName / elapsedSeconds / configuredTimeoutSec /
            // isCallerCancelled shape; this line just notes that the
            // segment's fail-soft fallback is now engaged. Operational
            // failures (JsonException, network error, unhandled bugs)
            // remain LogWarning so dashboards keep paging.
            //
            // Field names are SecretRedactionProcessor-safe (no "token" /
            // "prompt" / "secret" / "bearer" substring); same hygiene rule
            // as LlmCallDiagnostics.LogCancellation — see commit dd6b418.
            var exceptionType = ex.GetType().FullName;
            var innerExceptionType = ex.InnerException?.GetType().FullName ?? "<none>";
            var isSdkTimeoutCancellation = ex is OperationCanceledException
                && !cancellationToken.IsCancellationRequested;
            if (isSdkTimeoutCancellation)
            {
                _logger.LogInformation(
                    "Dovetail segment failed fail-soft (SDK timeout); returning fallback " +
                    "(exceptionType={ExceptionType}, innerExceptionType={InnerExceptionType}, " +
                    "cancellationRequested={CancellationRequested})",
                    exceptionType,
                    innerExceptionType,
                    cancellationToken.IsCancellationRequested);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Dovetail segment failed fail-soft (operational failure); returning fallback " +
                    "(exceptionType={ExceptionType}, innerExceptionType={InnerExceptionType}, " +
                    "cancellationRequested={CancellationRequested})",
                    exceptionType,
                    innerExceptionType,
                    cancellationToken.IsCancellationRequested);
            }
            return _fallbackFactory(input);
        }
    }
}
