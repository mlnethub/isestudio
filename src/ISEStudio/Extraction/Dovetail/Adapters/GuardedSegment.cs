using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps <paramref name="inner"/> in a job-level 409 envelope. When the
/// guard detects a concurrent request (job already running), it returns
/// <paramref name="conflictEnvelope"/> instead of the inner segment's
/// failure. Otherwise the inner exception propagates.
/// </summary>
public sealed class GuardedSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    IRunWithExtractionGuard guard,
    Func<TIn, TOut> conflictEnvelope) : IPipelineSegment<TIn, TOut>
{
    private readonly IPipelineSegment<TIn, TOut> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IRunWithExtractionGuard _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    private readonly Func<TIn, TOut> _conflictEnvelope = conflictEnvelope ?? throw new ArgumentNullException(nameof(conflictEnvelope));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        _guard.RunAsync(
            work: () => _inner.ExecuteAsync(input, cancellationToken),
            conflictEnvelope: () => _conflictEnvelope(input),
            ct: cancellationToken);
}
