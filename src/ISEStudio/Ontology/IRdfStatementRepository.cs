using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Ontology;

public interface IRdfStatementRepository
{
    Task ReplaceLayerAsync(Guid knowledgeSystemId, string layer, IReadOnlyList<RdfStatement> statements, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RdfStatement>> ListAsync(Guid knowledgeSystemId, string? layer = null, CancellationToken cancellationToken = default);
}
