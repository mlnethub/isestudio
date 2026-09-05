using System.Diagnostics;
using ISEStudio.Graph;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Graph;

public sealed class GraphPerformanceTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public GraphPerformanceTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Five_hop_neighborhood_of_one_thousand_facts_completes_within_two_seconds()
    {
        await _fixture.SeedGraphReferencesAsync();
        await _fixture.ResetGraphWritesAsync();
        var rootEntityId = await _fixture.CreateTraversalFixtureAsync(factCount: 1000);

        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var stopwatch = Stopwatch.StartNew();

        await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                rootEntityId,
                5,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }
}