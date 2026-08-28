using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

public class FailSoftSegmentTests
{
    private sealed record In(int Value);
    private sealed record Out(int Result, string? Tag);

    [Fact]
    public async Task ExecuteAsync_InnerThrows_ReturnsFallback()
    {
        IPipelineSegment<In, Out> inner = new ThrowingSegment<In, Out>();
        var seg = new FailSoftSegment<In, Out>(
            inner,
            fallbackFactory: _ => new Out(0, "fallback"),
            logger: NullLogger<FailSoftSegment<In, Out>>.Instance);

        var result = await seg.ExecuteAsync(new In(42), CancellationToken.None);

        Assert.Equal(0, result.Result);
        Assert.Equal("fallback", result.Tag);
    }

    [Fact]
    public async Task ExecuteAsync_InnerSucceeds_ReturnsInnerResult()
    {
        IPipelineSegment<In, Out> inner = new InlineSegment<In, Out>(
            (input, _) => Task.FromResult(new Out(input.Value * 2, "ok")));
        var seg = new FailSoftSegment<In, Out>(
            inner,
            fallbackFactory: _ => new Out(-1, "fallback"),
            logger: NullLogger<FailSoftSegment<In, Out>>.Instance);

        var result = await seg.ExecuteAsync(new In(5), CancellationToken.None);

        Assert.Equal(10, result.Result);
        Assert.Equal("ok", result.Tag);
    }
}
