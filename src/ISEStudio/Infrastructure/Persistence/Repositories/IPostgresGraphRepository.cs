using ISEStudio.Graph;

namespace ISEStudio.Infrastructure.Persistence.Repositories;

public interface IPostgresGraphRepository
{
    Task<GraphEntityType> CreateEntityTypeAsync(
        CreateEntityTypeCommand command,
        CancellationToken cancellationToken);

    Task<GraphRelationType> CreateRelationTypeAsync(
        CreateRelationTypeCommand command,
        CancellationToken cancellationToken);

    Task<GraphEntity> CreateEntityAsync(
        CreateGraphEntityCommand command,
        CancellationToken cancellationToken);

    Task<GraphFact> RecordFactAsync(
        RecordFactCommand command,
        CancellationToken cancellationToken);

    Task InvalidateFactAsync(
        Guid knowledgeSystemId,
        Guid factId,
        DateTimeOffset invalidatedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphFact>> QueryFactsAsync(
        Guid knowledgeSystemId,
        FactQuery query,
        CancellationToken cancellationToken);
}
