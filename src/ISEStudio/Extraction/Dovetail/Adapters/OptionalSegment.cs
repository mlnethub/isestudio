using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Allows a segment to be either present (delegate to <paramref name="inner"/>)
/// or absent (return <paramref name="noOpFactory"/>). DI decides which
/// registration wins; runtime null-check is unnecessary.
/// </summary>
public sealed class OptionalSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut>? inner,
    Func<TIn, TOut> noOpFactory) : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _noOpFactory = noOpFactory ?? throw new ArgumentNullException(nameof(noOpFactory));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        inner is null
            ? Task.FromResult(_noOpFactory(input))
            : inner.ExecuteAsync(input, cancellationToken);
}
