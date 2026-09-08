namespace ISEStudio.IntegrationTests.Benchmarks;

public sealed class IngestionBenchmarkTests : IClassFixture<BenchmarkFixture>
{
    private readonly BenchmarkFixture _fixture;

    public IngestionBenchmarkTests(BenchmarkFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Durable_batch_ingestion_emits_stable_metrics_and_hash()
    {
        await _fixture.MeasureAsync("ingestion", _fixture.RunIngestionAsync);
    }
}