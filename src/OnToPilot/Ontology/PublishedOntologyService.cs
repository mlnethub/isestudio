using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Ontology;

/// <summary>
/// Reads the curated TBox view from a published release's tbox.nq
/// shard (RDF 1.1 N-Quads on disk, no Oxigraph dependency). The
/// controller layer (PublishedController) handles scope check +
/// cache headers + release resolution; this service assumes those
/// have already happened.
/// </summary>
public sealed class PublishedOntologyService
{
    private readonly OnToPilotDbContext _db;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly OntologyViewBuilder _builder;

    public PublishedOntologyService(
        OnToPilotDbContext db,
        ReleaseArtifactStore artifacts,
        OntologyViewBuilder builder)
    {
        _db = db;
        _artifacts = artifacts;
        _builder = builder;
    }

    public async Task<OntologyResponse?> GetViewAsync(
        string publicId, string version, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (ks is null) return null;

        var release = await _db.OntologyReleases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.KnowledgeSystemId == ks.Id && r.Version == version,
                ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        var tboxShard = _artifacts.Read(release.Id.ToString(), RdfLayer.TBox);
        var view = await _builder
            .BuildFromNQuadsAsync(tboxShard, ct)
            .ConfigureAwait(false);

        return view with
        {
            KnowledgeSystem = new KnowledgeSystemMeta(
                Id: ks.Id,
                Name: ks.Name,
                BaseIri: ks.BaseIri,
                Release: release.Version),
        };
    }
}