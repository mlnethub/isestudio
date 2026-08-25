using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Ontology;

/// <summary>
/// Resolved bundle of the rows + serving-store handle backing a single
/// <c>published.*</c> request. Built once per request by
/// <see cref="PublishedDataService.ResolveAsync"/> so each method can stay
/// pure projection work instead of re-querying the KS/release/deployment.
/// </summary>
/// <remarks>
/// <see cref="ReleaseKey"/> is the <c>OntologyReleaseEntity.Id</c> in the
/// 32-char no-dash form <see cref="ReleaseManager"/> keys its
/// <c>_published</c> registry on (and what <see cref="ReleaseArtifactStore"/>
/// writes shards under). <see cref="Store"/> is a per-release read-only
/// Oxigraph instance the manager opened at publish time — different from
/// the live workspace <see cref="StoreWrapper"/> so workspace writes after
/// publication never leak into the served view.
/// </remarks>
/// <summary>
/// Resolved bundle of the rows + serving-store handle backing a single
/// <c>published.*</c> request. Built once per request by
/// <see cref="PublishedDataService.ResolveAsync"/> so each method can stay
/// pure projection work instead of re-querying the KS/release/deployment.
/// </summary>
/// <remarks>
/// <see cref="ReleaseKey"/> is the <c>OntologyReleaseEntity.Id</c> in the
/// 32-char no-dash form <see cref="ReleaseManager"/> keys its
/// <c>_published</c> registry on (and what <see cref="ReleaseArtifactStore"/>
/// writes shards under). <see cref="Store"/> is a per-release read-only
/// Oxigraph instance the manager opened at publish time — different from
/// the live workspace <see cref="StoreWrapper"/> so workspace writes after
/// publication never leak into the served view.
/// </remarks>
public sealed record ServingContext(
    KnowledgeSystemEntity Ks,
    OntologyReleaseEntity Release,
    ReleaseDeploymentEntity Deployment,
    string ReleaseKey,
    StoreWrapper Store);

/// <summary>
/// Scoped service backing the six <c>published.*</c> read endpoints the
/// OpenAPI baseline tags <c>published release api</c>:
/// <c>metadata</c>, <c>manifest</c>, <c>classes</c>, <c>export</c>,
/// <c>individual</c>, and <c>individuals</c> (each also reachable via the
/// <c>/releases/{version}/</c> pinned path). Mirrors the Python
/// <c>backend/app/api/published.py</c> shape.
///
/// <para>The controller (<see cref="Controllers.PublishedController"/>)
/// already enforces token verification, scope checks, release lifecycle
/// (503 / 410 / 404), and stamps <c>Cache-Control</c> +
/// <c>X-ISEStudio-Release</c> + <c>ETag</c>. This service assumes those
/// have already passed and produces only the wire body.</para>
/// </summary>
public sealed class PublishedDataService : IDisposable
{
    private readonly ISEStudioDbContext _db;
    private readonly ReleaseManager _releases;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly OntologyViewBuilder _viewBuilder;

    public PublishedDataService(
        ISEStudioDbContext db,
        ReleaseManager releases,
        ReleaseArtifactStore artifacts,
        OntologyViewBuilder viewBuilder)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(viewBuilder);
        _db = db;
        _releases = releases;
        _artifacts = artifacts;
        _viewBuilder = viewBuilder;
    }

    /// <summary>
    /// Dispose the owned <see cref="ISEStudioDbContext"/>. Mirrors the
    /// other slice services that own a scoped <c>DbContext</c> so tests
    /// can <c>using var svc = …</c> in the per-test fixture style.
    /// </summary>
    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Resolve the knowledge system + release + deployment + serving-store
    /// handle backing a <c>published.*</c> request. Pinned requests pass
    /// <paramref name="version"/>; current requests pass <c>null</c> and we
    /// walk back to the most-recent active deployment.
    /// </summary>
    /// <returns><c>null</c> when any link in the chain is missing —
    /// unknown public_id, unknown version, no current deployment, or the
    /// release has not been materialised into the serving store yet. The
    /// dispatcher falls back to the schema-compatible empty envelope in
    /// that case.</returns>
    public async Task<ServingContext?> ResolveAsync(
        string publicId, string? version, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (ks is null) return null;

        OntologyReleaseEntity? release;
        ReleaseDeploymentEntity? deployment;

        if (string.IsNullOrEmpty(version))
        {
            // Current: walk deployment → release. SQLite cannot ORDER BY
            // DateTimeOffset, so pull the rows client-side and sort in
            // memory, mirroring the controller-side ResolveReleaseAsync.
            deployment = (await _db.ReleaseDeployments.AsNoTracking()
                .Where(d => d.KnowledgeSystemId == ks.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();
            if (deployment is null) return null;
            release = await _db.OntologyReleases.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == deployment.ReleaseId, ct)
                .ConfigureAwait(false);
        }
        else
        {
            release = await _db.OntologyReleases.AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.KnowledgeSystemId == ks.Id && r.Version == version,
                    ct)
                .ConfigureAwait(false);
            if (release is null) return null;
            deployment = await _db.ReleaseDeployments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.ReleaseId == release.Id, ct)
                .ConfigureAwait(false);
        }
        if (release is null) return null;

        return BuildContext(ks, release, deployment);
    }

    private ServingContext? BuildContext(
        KnowledgeSystemEntity ks,
        OntologyReleaseEntity release,
        ReleaseDeploymentEntity? deployment)
    {
        var key = release.Id.ToString("N");
        // IsPublished lazy-opens the on-disk serving directory if one
        // exists, so a process restart can serve a previously-published
        // release without re-publishing.
        if (!_releases.IsPublished(key)) return null;

        var store = ResolveServingStore(key);
        if (store is null) return null;

        // Ensure the deployment row exists for the response projection —
        // synthesise a minimal active deployment when a published release
        // hasn't been deployed yet. Keeps the response shape stable so the
        // frontend's "active deployment" badge degrades gracefully.
        if (deployment is null)
        {
            var ksc = new KsContext(ks.GraphIri, ks.BaseIri);
            deployment = new ReleaseDeploymentEntity
            {
                Id = Guid.NewGuid(),
                KnowledgeSystemId = ks.Id,
                ReleaseId = release.Id,
                Status = "active",
                TboxGraphIri = ksc.TBoxGraph,
                VocabularyGraphIri = ksc.VocabularyGraph,
                AboxGraphIri = ksc.ABoxGraph,
                StatementCount = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                ActivatedAt = DateTimeOffset.UtcNow,
            };
        }

        return new ServingContext(ks, release, deployment, key, store);
    }

    /// <summary>
    /// Open a fresh read-only <see cref="StoreWrapper"/> on the serving
    /// directory. Used so the <c>classes</c> / <c>individual</c> /
    /// <c>individuals</c> methods can call <c>StoreWrapper.Match</c>
    /// directly with subject / graph IRI filters. We re-open rather than
    /// reach into the manager's in-memory registry so this service stays
    /// side-effect-free (a concurrent publish / delete in another request
    /// doesn't tear down our handle mid-read).
    /// </summary>
    private StoreWrapper? ResolveServingStore(string releaseKey)
    {
        var path = _releases.ServingPath(releaseKey);
        if (!Directory.Exists(path)) return null;
        try
        {
            return StoreWrapper.OpenReadOnly(path);
        }
        catch
        {
            return null;
        }
    }

    // ----------------------------------------------------------------------
    // metadata
    // ----------------------------------------------------------------------

    /// <summary>
    /// Project the Python <c>get_release_metadata</c> shape: KS metadata
    /// + a nested release sub-object + stats + the active token scopes
    /// (the dispatcher passes them through).
    /// </summary>
    public Task<object?> GetMetadataAsync(
        ServingContext ctx, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var stats = BuildStats(ctx);
        var manifestSha = TryGetManifestSha(ctx.Release);
        var publishedAt = ctx.Release.PublishedAt?.ToString("O");

        var body = new Dictionary<string, object?>
        {
            ["id"] = ctx.Ks.PublicId,
            ["name"] = ctx.Ks.Name,
            ["description"] = ctx.Ks.Description ?? string.Empty,
            ["baseIri"] = ctx.Ks.BaseIri,
            ["release"] = new Dictionary<string, object?>
            {
                ["id"] = ctx.Release.Id,
                ["version"] = ctx.Release.Version,
                ["publishedAt"] = publishedAt,
                ["manifestSha256"] = manifestSha,
            },
            ["stats"] = stats,
            ["scopes"] = scopes ?? Array.Empty<string>(),
        };
        return Task.FromResult<object?>(body);
    }

    /// <summary>
    /// MVP placeholder for <c>controlled_terms</c>: Python reads the vocab
    /// graph from the serving store and asks <c>skos.build_view</c> for
    /// the concept count. The .NET vocab-graph serving path is deferred;
    /// until then we report 0 so the frontend's stat badge degrades
    /// gracefully rather than 500ing.
    /// </summary>
    private static Dictionary<string, object?> BuildStats(ServingContext ctx)
    {
        return new Dictionary<string, object?>
        {
            ["statements"] = ctx.Deployment.StatementCount,
            ["controlledTerms"] = 0,
        };
    }

    private static string? TryGetManifestSha(OntologyReleaseEntity release)
    {
        if (release.Manifest is null) return null;
        if (!release.Manifest.RootElement.TryGetProperty("manifest_file", out var file)) return null;
        if (file.ValueKind != JsonValueKind.Object) return null;
        if (!file.TryGetProperty("sha256", out var sha)) return null;
        return sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
    }

    // ----------------------------------------------------------------------
    // manifest
    // ----------------------------------------------------------------------

    /// <summary>
    /// Return the manifest JSON column verbatim. Mirrors
    /// <c>access.release.manifest</c> in the Python backend. The controller
    /// already stamps <c>ETag</c> from the canonical manifest JSON, so this
    /// body intentionally just echoes the same bytes.
    /// </summary>
    public object? GetManifest(ServingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Release.Manifest is null)
        {
            return JsonDocument.Parse("""{"capture_status":"pending"}""").RootElement.Clone();
        }
        return ctx.Release.Manifest.RootElement.Clone();
    }

    // ----------------------------------------------------------------------
    // classes
    // ----------------------------------------------------------------------

    /// <summary>
    /// Project the Python <c>list_release_classes</c> shape: every class
    /// IRI in the TBox + its <c>rdfs:label</c> + an instance count from
    /// the ABox. Sorted by <c>(-count, label)</c> exactly as Python does
    /// so the frontend's class table renders identically.
    /// </summary>
    public async Task<object?> GetClassesAsync(
        ServingContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Read the tbox shard from disk (same path PublishedOntologyService
        // takes for the wire-shape-parity reasons documented there).
        var tboxShard = _artifacts.Read(ctx.ReleaseKey, RdfLayer.TBox);
        var tboxView = await _viewBuilder
            .BuildFromNQuadsAsync(tboxShard, ct)
            .ConfigureAwait(false);
        var labels = tboxView.Labels;

        // Counts via a single sweep of the abox graph in the serving
        // store. Mirrors ABoxManager.CountsByClass but reads from the
        // serving store rather than the live workspace handle.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var aboxQuads = ctx.Store.Match(graphIri: ctx.Deployment.AboxGraphIri);
        foreach (var q in aboxQuads)
        {
            if (q.Subject is not OntoNamedNode) continue;
            if (q.Predicate.Value != Vocabulary.RdfType.Value) continue;
            if (q.Object is not OntoNamedNode cls) continue;
            if (cls.Value == Vocabulary.OwlNamedIndividual.Value) continue;
            counts.TryGetValue(cls.Value, out var n);
            counts[cls.Value] = n + 1;
        }

        var classes = tboxView.Classes
            .Select(c =>
            {
                var iri = c.Iri;
                return new Dictionary<string, object?>
                {
                    ["iri"] = iri,
                    ["label"] = c.Label ?? (labels.TryGetValue(iri, out var l) ? l : LocalIri(iri)),
                    ["count"] = counts.TryGetValue(iri, out var n) ? n : 0,
                };
            })
            // Python sort key: (-count, label). Replicate in-place so the
            // most-populous class renders first.
            .OrderByDescending(c => (int)c["count"]!)
            .ThenBy(c => (string)c["label"]!, StringComparer.Ordinal)
            .ToList();

        var total = counts.Values.Sum();
        var body = new Dictionary<string, object?>
        {
            ["classes"] = classes,
            ["total"] = total,
        };
        return body;
    }

    // ----------------------------------------------------------------------
    // export
    // ----------------------------------------------------------------------

    /// <summary>
    /// Read the TBox shard and return it as the export payload. The
    /// dispatcher throws the returned bytes via
    /// <see cref="ExportFilePayloadException"/> so
    /// <see cref="FastApiErrorMiddleware"/> can write the raw response
    /// without a JSON envelope — mirrors Python's
    /// <c>Response(content=content, media_type="application/n-quads")</c> on
    /// <c>backend/app/api/published.py:181</c>.
    /// </summary>
    /// <remarks>
    /// MVP emits <c>application/n-quads</c> only — the Python backend
    /// accepts <c>?fmt=turtle|nquads|json-ld|...</c> and switches on the
    /// Oxigraph serializer; the .NET port defers that switch until
    /// RdfExportService gains a serving-store overload.
    /// </remarks>
    public byte[] GetExport(ServingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return _artifacts.Read(ctx.ReleaseKey, RdfLayer.TBox);
    }

    // ----------------------------------------------------------------------
    // individual
    // ----------------------------------------------------------------------

    /// <summary>
    /// Read one individual from the sealed release's ABox graph.
    /// Returns <c>null</c> when the IRI has no triples — the dispatcher
    /// surfaces that as 404.
    /// </summary>
    public async Task<IndividualOut?> GetIndividualAsync(
        ServingContext ctx, string iri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(iri);

        var outgoing = ctx.Store.Match(
            subjectIri: iri, graphIri: ctx.Deployment.AboxGraphIri);
        if (outgoing.Count == 0) return null;

        var (classLabels, propLabels) = await BuildLabelMapsAsync(ctx, ct)
            .ConfigureAwait(false);

        var types = new List<LabeledIri>();
        var objectAssertions = new List<ObjectAssertionOut>();
        var dataAssertions = new List<DataAssertionOut>();
        string? label = null;

        foreach (var quad in outgoing)
        {
            if (quad.Predicate.Value == Vocabulary.RdfType.Value
                && quad.Object is OntoNamedNode cls)
            {
                var clsIri = cls.Value;
                if (clsIri == Vocabulary.OwlNamedIndividual.Value) continue;
                types.Add(new LabeledIri(clsIri,
                    classLabels.TryGetValue(clsIri, out var l) ? l : LocalIri(clsIri)));
            }
            else if (quad.Predicate.Value == Vocabulary.RdfsLabel.Value
                && quad.Object is OntoLiteral labelLit)
            {
                label = labelLit.Value;
            }
            else if (quad.Object is OntoNamedNode target)
            {
                var propIri = quad.Predicate.Value;
                objectAssertions.Add(new ObjectAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Target: target.Value,
                    TargetLabel: LocalIri(target.Value),
                    Sources: Array.Empty<string>()));
            }
            else if (quad.Object is OntoLiteral literal)
            {
                var propIri = quad.Predicate.Value;
                dataAssertions.Add(new DataAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Value: literal.Value,
                    Datatype: literal.Datatype?.Value,
                    Sources: Array.Empty<string>()));
            }
        }

        return new IndividualOut(
            Iri: iri,
            Label: label ?? LocalIri(iri),
            Types: types,
            ObjectAssertions: objectAssertions,
            DataAssertions: dataAssertions);
    }

    // ----------------------------------------------------------------------
    // individuals
    // ----------------------------------------------------------------------

    /// <summary>
    /// Enumerate every individual in the sealed release's ABox with
    /// optional class + label-text narrowing. Mirrors the Python
    /// <c>abox.list_individuals</c> shape.
    /// </summary>
    public async Task<IndividualsOut?> ListIndividualsAsync(
        ServingContext ctx, string? classIri, string? q,
        int limit, int offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var (classLabels, _) = await BuildLabelMapsAsync(ctx, ct)
            .ConfigureAwait(false);

        var classBySubject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var labelBySubject = new Dictionary<string, string>(StringComparer.Ordinal);
        var allQuads = ctx.Store.Match(graphIri: ctx.Deployment.AboxGraphIri);
        foreach (var quad in allQuads)
        {
            if (quad.Subject is not OntoNamedNode subj) continue;
            var siri = subj.Value;
            if (quad.Predicate.Value == Vocabulary.RdfType.Value
                && quad.Object is OntoNamedNode cls)
            {
                if (!classBySubject.TryGetValue(siri, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    classBySubject[siri] = set;
                }
                set.Add(cls.Value);
            }
            else if (quad.Predicate.Value == Vocabulary.RdfsLabel.Value
                && quad.Object is OntoLiteral lit)
            {
                labelBySubject[siri] = lit.Value;
            }
        }

        var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var filtered = classBySubject.Keys
            .Where(s => classIri is null || classBySubject[s].Contains(classIri))
            .Where(s =>
            {
                if (needle is null) return true;
                if (labelBySubject.TryGetValue(s, out var lbl)
                    && lbl.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return s.Contains(needle, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(s => labelBySubject.TryGetValue(s, out var l) ? l : LocalIri(s),
                StringComparer.Ordinal)
            .ToList();

        var total = filtered.Count;
        var page = filtered.Skip(offset).Take(limit).ToList();
        var items = new List<IndividualListItem>(page.Count);
        foreach (var iri in page)
        {
            var label = labelBySubject.TryGetValue(iri, out var l) ? l : LocalIri(iri);
            var types = classBySubject[iri]
                .Where(t => t != Vocabulary.OwlNamedIndividual.Value)
                .OrderBy(t => t, StringComparer.Ordinal)
                .Select(t => new LabeledIri(t,
                    classLabels.TryGetValue(t, out var tl) ? tl : LocalIri(t)))
                .ToList();
            items.Add(new IndividualListItem(iri, label, types));
        }

        return new IndividualsOut(items, total);
    }

    // ----------------------------------------------------------------------
    // shared helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Class + property labels for the ABox row projection. Mirrors
    /// Python's <c>_labels(tbox_graph_iri)</c> at
    /// <c>published.py:98-105</c> — read the TBox once and surface both
    /// maps.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> ClassLabels,
                        IReadOnlyDictionary<string, string> PropLabels)>
        BuildLabelMapsAsync(ServingContext ctx, CancellationToken ct)
    {
        var tboxShard = _artifacts.Read(ctx.ReleaseKey, RdfLayer.TBox);
        var view = await _viewBuilder
            .BuildFromNQuadsAsync(tboxShard, ct)
            .ConfigureAwait(false);
        return (view.Labels, view.Labels);
    }

    /// <summary>
    /// Local-name fragment of an IRI (last segment after <c>#</c>,
    /// <c>/</c>, or <c>:</c>). Mirrors Python <c>backend.local_name</c> +
    /// the same helper in <see cref="ABoxManager"/>.
    /// </summary>
    private static string LocalIri(string iri)
    {
        var hashIdx = iri.LastIndexOf('#');
        var slashIdx = iri.LastIndexOf('/');
        var colonIdx = iri.LastIndexOf(':');
        var idx = Math.Max(hashIdx, Math.Max(slashIdx, colonIdx));
        return idx >= 0 ? iri[(idx + 1)..] : iri;
    }
}