using Dovetail;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps <paramref name="inner"/> and converts exceptions into a fallback
/// result. Operational failures (anything other than
/// <see cref="OperationCanceledException"/> when cancellation is requested)
/// are logged and routed to <paramref name="fallbackFactory"/>.
/// Strictly aligned with Python fail-soft semantics.
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
            _logger.LogWarning(ex, "Dovetail segment failed fail-soft; returning fallback");
            return _fallbackFactory(input);
        }
    }
}
