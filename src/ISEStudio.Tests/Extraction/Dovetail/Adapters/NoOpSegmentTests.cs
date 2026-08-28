using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

public class NoOpSegmentTests
{
    private sealed record In(int X);
    private sealed record Out();

    [Fact]
    public async Task ExecuteAsync_ReturnsFactoryResult()
    {
        var seg = new NoOpSegment<In, Out>(_ => new Out());

        var result = await seg.ExecuteAsync(new In(99), CancellationToken.None);

        Assert.NotNull(result);
    }
}
