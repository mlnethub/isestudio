namespace ISEStudio.IntegrationTests.Benchmarks;

public sealed class SearchBenchmarkTests : IClassFixture<BenchmarkFixture>
{
    private readonly BenchmarkFixture _fixture;

    public SearchBenchmarkTests(BenchmarkFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Full_text_search_emits_stable_metrics_and_hash()
    {
        await _fixture.MeasureAsync("search", _fixture.RunSearchAsync);
    }
}