using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Adapters;

public class GuardedSegmentTests
{
    private sealed record In(int Value);
    private sealed record Out(int Value, string Source);

    [Fact]
    public async Task ExecuteAsync_InnerSucceeds_ReturnsInnerResult()
    {
        IPipelineSegment<In, Out> inner = new InlineSegment<In, Out>(
            (input, _) => Task.FromResult(new Out(input.Value, "ok")));
        var seg = new GuardedSegment<In, Out>(
            inner, guard: new FakeGuard { ThrowConflict = false },
            conflictEnvelope: _ => new Out(-409, "conflict"));

        var result = await seg.ExecuteAsync(new In(5), CancellationToken.None);

        Assert.Equal(5, result.Value);
        Assert.Equal("ok", result.Source);
    }

    [Fact]
    public async Task ExecuteAsync_GuardTranslatesToConflict_ReturnsEnvelope()
    {
        IPipelineSegment<In, Out> inner = new ThrowingSegment<In, Out>();
        var seg = new GuardedSegment<In, Out>(
            inner, guard: new FakeGuard { ThrowConflict = true },
            conflictEnvelope: _ => new Out(-409, "conflict"));

        var result = await seg.ExecuteAsync(new In(5), CancellationToken.None);

        Assert.Equal(-409, result.Value);
        Assert.Equal("conflict", result.Source);
    }

    private sealed class FakeGuard : IRunWithExtractionGuard
    {
        public bool ThrowConflict { get; init; }

        public Task<T> RunAsync<T>(Func<Task<T>> work, Func<T> conflictEnvelope, CancellationToken ct)
        {
            if (ThrowConflict)
            {
                return Task.FromResult(conflictEnvelope());
            }
            return work();
        }
    }
}
