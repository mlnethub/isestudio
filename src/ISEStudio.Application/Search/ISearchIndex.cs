namespace ISEStudio.Application.Search;

/// <summary>Provider-neutral search request scoped to one knowledge system.</summary>
public sealed record SearchRequest
{
    public SearchRequest(
        Guid KnowledgeSystemId,
        string Query,
        int Limit = 20,
        int Offset = 0,
        DateTimeOffset? AsOf = null,
        Guid? ActorId = null,
        IReadOnlyList<float>? QueryVector = null)
    {
        if (KnowledgeSystemId == Guid.Empty)
        {
            throw new ArgumentException("Knowledge system id is required.", nameof(KnowledgeSystemId));
        }
        if (string.IsNullOrWhiteSpace(Query))
        {
            throw new ArgumentException("Search query is required.", nameof(Query));
        }
        if (Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), "Limit must be between 1 and 100.");
        }
        if (Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset), "Offset cannot be negative.");
        }

        this.KnowledgeSystemId = KnowledgeSystemId;
        this.Query = Query.Trim();
        this.Limit = Limit;
        this.Offset = Offset;
        this.AsOf = AsOf;
        this.ActorId = ActorId;
        this.QueryVector = QueryVector;
    }

    public Guid KnowledgeSystemId { get; }
    public string Query { get; }
    public int Limit { get; }
    public int Offset { get; }
    public DateTimeOffset? AsOf { get; }
    public Guid? ActorId { get; }
    public IReadOnlyList<float>? QueryVector { get; }
}

/// <summary>A single immutable document-version chunk returned by search.</summary>
public sealed record SearchHit(
    Guid ChunkId,
    Guid DocumentId,
    string Text,
    double LexicalScore,
    double? VectorScore,
    string SourceSha256);

/// <summary>Capabilities exposed without leaking the backing search provider.</summary>
public sealed record SearchCapabilities(bool SupportsVectorSearch)
{
    public static SearchCapabilities PostgresWithoutVector { get; } = new(false);
}

public interface ISearchIndex
{
    SearchCapabilities Capabilities { get; }

    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}