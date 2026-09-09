using ISEStudio.Ontology;

namespace ISEStudio.Tests.Infrastructure;

/// <summary>
/// DI-shape stub for <see cref="IRdfStatementRepository"/>. The migration
/// off Oxigraph made the repository a required ctor dependency on
/// <see cref="ISEStudio.Extraction.TerminologyService"/> / the conflict and
/// structure agents, so pipeline-resolution tests can no longer pass a null
/// store. Those tests never invoke the repository — any call fails loudly
/// instead of silently no-oping.
/// </summary>
internal sealed class StubRdfStatementRepository : IRdfStatementRepository
{
    public Task ReplaceLayerAsync(Guid knowledgeSystemId, string layer,
        IReadOnlyList<RdfStatement> statements, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Stub repository — not expected to be called.");

    public Task<IReadOnlyList<RdfStatement>> ListAsync(Guid knowledgeSystemId, string? layer = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Stub repository — not expected to be called.");
}
