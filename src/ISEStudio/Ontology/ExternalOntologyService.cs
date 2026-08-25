using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;

namespace ISEStudio.Ontology;

/// <summary>
/// Reads the curated TBox view for the public API surface
/// (/api/v1/knowledge-systems/{public_id}/ontology). Resolves the
/// KS by public id (NOT internal Guid — external callers never see
/// the internal id). Attaches ExternalKnowledgeSystemMeta with
/// public_id (string) instead of the Guid variant.
/// </summary>
public sealed class ExternalOntologyService
{
    private readonly ISEStudioDbContext _db;
    private readonly StoreWrapper? _store;
    private readonly OntologyViewBuilder _builder;

    public ExternalOntologyService(
        ISEStudioDbContext db,
        StoreWrapper? store,
        OntologyViewBuilder builder)
    {
        _db = db;
        _store = store;
        _builder = builder;
    }

    public async Task<OntologyResponse?> GetViewAsync(
        string publicId, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (ks is null) return null;

        var view = await _builder
            .BuildFromStoreAsync(_store, ks.GraphIri, ct)
            .ConfigureAwait(false);

        return view with
        {
            KnowledgeSystem = new ExternalKnowledgeSystemMeta(
                PublicId: ks.PublicId,
                Name: ks.Name,
                BaseIri: ks.BaseIri),
        };
    }
}