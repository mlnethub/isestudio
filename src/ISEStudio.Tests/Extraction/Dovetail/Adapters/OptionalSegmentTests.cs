using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

public class OptionalSegmentTests
{
    private sealed record In(int X);
    private sealed record Out(int Y, string Source);

    [Fact]
    public async Task ExecuteAsync_NullInner_ReturnsNoOpFactoryResult()
    {
        IPipelineSegment<In, Out>? inner = null;
        var seg = new OptionalSegment<In, Out>(
            inner,
            noOpFactory: _ => new Out(-1, "noop"));

        var result = await seg.ExecuteAsync(new In(7), CancellationToken.None);

        Assert.Equal(-1, result.Y);
        Assert.Equal("noop", result.Source);
    }

    [Fact]
    public async Task ExecuteAsync_NonNullInner_DelegatesToInner()
    {
        IPipelineSegment<In, Out> inner = new InlineSegment<In, Out>(
            (input, _) => Task.FromResult(new Out(input.X + 100, "real")));
        var seg = new OptionalSegment<In, Out>(inner, noOpFactory: _ => new Out(0, "noop"));

        var result = await seg.ExecuteAsync(new In(1), CancellationToken.None);

        Assert.Equal(101, result.Y);
        Assert.Equal("real", result.Source);
    }
}
