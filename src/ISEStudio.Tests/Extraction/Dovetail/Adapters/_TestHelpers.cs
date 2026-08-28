using Dovetail;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

internal sealed class InlineSegment<TIn, TOut>(
    Func<TIn, CancellationToken, Task<TOut>> execute)
    : IPipelineSegment<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) => execute(input, ct);
}

internal sealed class ThrowingSegment<TIn, TOut>(string? message = null)
    : IPipelineSegment<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) =>
        throw new InvalidOperationException(message ?? "boom");
}
