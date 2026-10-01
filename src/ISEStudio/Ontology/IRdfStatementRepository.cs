using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Ontology;

public interface IRdfStatementRepository
{
    Task<RdfWriteScope> BeginWriteAsync(Guid knowledgeSystemId, CancellationToken cancellationToken = default)
        => Task.FromResult(RdfWriteScope.Unmanaged());

    Task<RdfWriteScope> BeginCaptureWriteAsync(Guid knowledgeSystemId, CancellationToken cancellationToken = default)
        => BeginWriteAsync(knowledgeSystemId, cancellationToken);

    Task<bool> AppendIfAbsentAsync(Guid knowledgeSystemId, string layer, RdfStatement statement, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Append requires a transactional RDF repository.");

    Task ReplaceLayerAsync(Guid knowledgeSystemId, string layer, IReadOnlyList<RdfStatement> statements, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RdfStatement>> ListAsync(Guid knowledgeSystemId, string? layer = null, CancellationToken cancellationToken = default);
}
