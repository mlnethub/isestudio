using ISEStudio.Application.Search;

namespace ISEStudio.Tests.Search;

public sealed class PostgresSearchIndexTests
{
    [Fact]
    public void Search_request_requires_a_bounded_query_and_limit()
    {
        Assert.Throws<ArgumentException>(() => new SearchRequest(
            Guid.NewGuid(),
            "   ",
            Limit: 20));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchRequest(
            Guid.NewGuid(),
            "pump",
            Limit: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchRequest(
            Guid.NewGuid(),
            "pump",
            Offset: -1));
    }

    [Fact]
    public void Search_request_requires_an_actor_for_authorized_search()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new SearchRequest(
            Guid.NewGuid(),
            "pump"));
    }

    [Fact]
    public void Search_contract_exposes_provider_neutral_hits_and_capabilities()
    {
        var hit = new SearchHit(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "pump pressure",
            LexicalScore: 0.75,
            VectorScore: null,
            SourceSha256: new string('a', 64));

        Assert.Equal(0.75, hit.LexicalScore);
        Assert.Null(hit.VectorScore);
        Assert.False(SearchCapabilities.NoVectorSearch.SupportsVectorSearch);
    }

    [Fact]
    public void Permission_filter_is_part_of_the_application_contract()
    {
        var actorId = Guid.NewGuid();
        var request = new SearchRequest(
            Guid.NewGuid(),
            "pump",
            ActorId: actorId,
            AsOf: DateTimeOffset.UtcNow);

        Assert.Equal(actorId, request.ActorId);
        Assert.NotNull(request.AsOf);
    }
}