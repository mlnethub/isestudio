using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using Oxigraph;

namespace OnToPilot.Ontology;

/// <summary>
/// Public read-only API surface for external token holders
/// (<c>/api/v1/knowledge-systems/{public_id}/*</c> minus ontology/query
/// which have their own services). Resolves the KS by public id (NOT the
/// internal Guid) and reads directly from the low-level managers — it
/// deliberately bypasses <see cref="ABoxService"/>'s KSRole gate because
/// a token actor's id is the token Guid, not a user id, so
/// <see cref="Authorization.KnowledgeSystemAccessService"/> would resolve
/// <see cref="Authorization.KSRole.None"/>. Token scope + KS-binding are
/// already enforced by <c>ExternalApiController.DispatchAsync</c> before
/// this service runs. Mirrors <c>backend/app/api/external.py</c>.
/// </summary>
public sealed class ExternalApiService
{
    private readonly OnToPilotDbContext _db;
    private readonly StoreWrapper? _store;
    private readonly OntologyViewBuilder _builder;
    private readonly ABoxManager _abox;
    private readonly SkosManager _skos;
    private readonly RdfExportService _export;

    public ExternalApiService(
        OnToPilotDbContext db,
        StoreWrapper? store,
        OntologyViewBuilder builder,
        ABoxManager abox,
        SkosManager skos,
        RdfExportService export)
    {
        _db = db;
        _store = store;
        _builder = builder;
        _abox = abox;
        _skos = skos;
        _export = export;
    }

    // ------------------------------------------------------------------
    // metadata (GET /{public_id})
    // ------------------------------------------------------------------

    /// <summary>
    /// Public metadata envelope. The <c>scopes</c> field the Python
    /// baseline echoes (<c>access.token.scopes</c>) is omitted because
    /// the token scopes are not threaded through
    /// <see cref="Actor"/>; every other field matches
    /// <c>external.get_public_metadata</c>. Returns <c>null</c> when no
    /// KS matches so the dispatcher can fall back to the placeholder.
    /// </summary>
    public async Task<object?> GetMetadataAsync(
        string publicId, Actor actor, CancellationToken ct)
    {
        var ks = await ResolveKsAsync(publicId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var ksc = KsContext.FromEntity(ks);
        var conceptCount = _skos.BuildView(ksc).Stats.ConceptCount;
        return new
        {
            id = ks.PublicId,
            name = ks.Name,
            description = ks.Description,
            base_iri = ks.BaseIri,
            stats = new
            {
                classes = ks.ClassCount,
                properties = ks.PropertyCount,
                axioms = ks.AxiomCount,
                controlled_terms = conceptCount,
            },
        };
    }

    // ------------------------------------------------------------------
    // classes (GET /{public_id}/classes)
    // ------------------------------------------------------------------

    /// <summary>
    /// TBox classes with the per-class ABox individual count, sorted by
    /// <c>(-count, label)</c> to match <c>list_public_classes</c> +
    /// <c>abox.counts_by_class</c>. Classes with no individuals still
    /// appear (count zero) so the UI can render <c>Animal (0)</c>.
    /// </summary>
    public async Task<ClassesOut?> ListClassesAsync(
        string publicId, Actor actor, CancellationToken ct)
    {
        var ks = await ResolveKsAsync(publicId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var ksc = KsContext.FromEntity(ks);
        var (classLabels, _) = await LoadLabelsAsync(ks, ct).ConfigureAwait(false);
        var counts = _abox.CountsByClass(ksc);
        var entries = classLabels
            .Select(kv => new ClassEntry(
                kv.Key, kv.Value,
                counts.TryGetValue(kv.Key, out var n) ? n : 0))
            .ToList();
        entries.Sort((a, b) =>
        {
            var cmp = b.Count.CompareTo(a.Count);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Label, b.Label);
        });
        return new ClassesOut(entries, counts.Values.Sum());
    }

    // ------------------------------------------------------------------
    // export (GET /{public_id}/export?fmt=)
    // ------------------------------------------------------------------

    /// <summary>
    /// Serialize the TBox graph in <paramref name="format"/>. The
    /// dispatcher arm parses the raw <c>fmt</c> query string via
    /// <see cref="Api.ValidationException"/>-throwing
    /// <c>ParseExportFormat</c> so an unsupported format surfaces as
    /// HTTP 400, matching Python
    /// <c>HTTPException(400, "Unsupported format")</c>. Returns
    /// <c>null</c> when the KS is unknown.
    /// </summary>
    public async Task<string?> ExportAsync(
        string publicId, RdfFormat format, Actor actor, CancellationToken ct)
    {
        var ks = await ResolveKsAsync(publicId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var bytes = await _export.ExportAsync(
            KsContext.FromEntity(ks), RdfLayer.TBox, format, ct)
            .ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // ------------------------------------------------------------------
    // individual (GET /{public_id}/individual?iri=)
    // ------------------------------------------------------------------

    /// <summary>
    /// Full individual envelope (types + object/data assertions).
    /// Returns <c>null</c> when the IRI has no ABox quads so the
    /// dispatcher maps to the empty-ref placeholder. Mirrors
    /// <c>external.get_public_individual</c> minus the
    /// <c>provenance:read</c> <c>sources</c> attachment (deferred to a
    /// later slice, same as <see cref="ABoxService"/>).
    /// </summary>
    public async Task<IndividualOut?> GetIndividualAsync(
        string publicId, string iri, Actor actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var ks = await ResolveKsAsync(publicId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var ksc = KsContext.FromEntity(ks);
        var (classLabels, propLabels) = await LoadLabelsAsync(ks, ct).ConfigureAwait(false);
        return _abox.GetIndividual(ksc, iri, classLabels, propLabels);
    }

    // ------------------------------------------------------------------
    // individuals (GET /{public_id}/individuals)
    // ------------------------------------------------------------------

    /// <summary>
    /// Paginated individual listing with optional <c>class_iri</c> /
    /// <c>q</c> filter. Mirrors <c>list_public_individuals</c>.
    /// </summary>
    public async Task<IndividualsOut?> ListIndividualsAsync(
        string publicId, string? classIri, string? q,
        int limit, int offset, Actor actor, CancellationToken ct)
    {
        var ks = await ResolveKsAsync(publicId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var ksc = KsContext.FromEntity(ks);
        var (classLabels, _) = await LoadLabelsAsync(ks, ct).ConfigureAwait(false);
        var items = _abox.ListIndividualsPaged(ksc, classLabels, classIri, q, offset, limit);
        var total = _abox.CountIndividualsPaged(ksc, classIri, q);
        return new IndividualsOut(items, total);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private async Task<KnowledgeSystemEntity?> ResolveKsAsync(string publicId, CancellationToken ct) =>
        await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Class + property label dicts built from the TBox view, mirroring
    /// Python <c>_labels()</c> (label-or-iri fallback). Both dicts are
    /// empty when the store is null (contract-test path) so the listing
    /// endpoints degrade to empty envelopes instead of crashing.
    /// </summary>
    private async Task<(Dictionary<string, string> classLabels, Dictionary<string, string> propLabels)> LoadLabelsAsync(
        KnowledgeSystemEntity ks, CancellationToken ct)
    {
        var view = await _builder.BuildFromStoreAsync(_store, ks.GraphIri, ct)
            .ConfigureAwait(false);
        var classLabels = view.Classes
            .ToDictionary(c => c.Iri, c => c.Label ?? c.Iri, StringComparer.Ordinal);
        var propLabels = view.ObjectProperties.Concat(view.DataProperties)
            .ToDictionary(p => p.Iri, p => p.Label ?? p.Iri, StringComparer.Ordinal);
        return (classLabels, propLabels);
    }
}
