namespace ISEStudio.IntegrationTests.Benchmarks;

public sealed class GraphTraversalBenchmarkTests : IClassFixture<BenchmarkFixture>
{
    private readonly BenchmarkFixture _fixture;

    public GraphTraversalBenchmarkTests(BenchmarkFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Bounded_recursive_traversal_emits_stable_metrics_and_hash()
    {
        await _fixture.MeasureAsync("graph_traversal", _fixture.RunTraversalAsync);
    }

    [Fact]
    public async Task Concurrent_graph_writes_emit_stable_metrics_and_hash()
    {
        await _fixture.MeasureAsync("concurrent_graph_writes", _fixture.RunConcurrentGraphWritesAsync);
    }
}