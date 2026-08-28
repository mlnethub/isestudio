using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Always returns <paramref name="factory"/> result. Used for placeholder
/// registration when a feature is disabled at startup.
/// </summary>
public sealed class NoOpSegment<TIn, TOut>(Func<TIn, TOut> factory) : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        Task.FromResult(_factory(input));
}
