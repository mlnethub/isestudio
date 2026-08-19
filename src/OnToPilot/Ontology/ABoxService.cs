using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

/// <summary>
/// ABox (instance) management surface for the .NET port. Mirrors
/// the Python <c>backend/app/api/abox.py</c> individual-CRUD endpoints:
/// <c>list_classes</c>, <c>list_individuals</c>, <c>get_individual</c>,
/// <c>create_individual</c>, <c>delete_individual</c>.
///
/// <para>Role gates mirror the Python dependencies: list / get require
/// <see cref="KSRole.Viewer"/>; create / delete require
/// <see cref="KSRole.Editor"/> on the KS (or admin). Mutations check the
/// <see cref="ExtractionJobStore"/> via
/// <see cref="InternalOperationDispatcher.RunWithExtractionGuardAsync"/>
/// (wired in the dispatcher arm) so a write that lands during a
/// running extraction is rejected with HTTP 409 + job id envelope.</para>
///
/// <para>The audit pipeline mirrors <see cref="OntologyService"/>:
/// pre/post <see cref="StoreWrapper.DumpNQuads"/> +
/// <see cref="StoreWrapper.DiffNQuads"/> populates the audit row's
/// Added/Removed byte[] so the history replay can roll back the change.
/// Provenance write-back (<c>AboxProvenanceEntity</c> rows) lands in a
/// later slice when <c>ABoxProvenanceService</c> is wired &mdash; for
/// B7a we write the audit row but leave the <c>ind_key</c> provenance
/// surface on the read path as empty arrays.</para>
/// </summary>
public sealed class ABoxService
{
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly ABoxManager _manager;
    private readonly StoreWrapper _store;

    public ABoxService(
        OnToPilotDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        ABoxManager manager,
        StoreWrapper store)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _manager = manager;
        _store = store;
    }

    // ----------------------------------------------------------------------
    // List / get
    // ----------------------------------------------------------------------

    /// <summary>
    /// Sidebar data: every TBox class with its ABox individual count.
    /// Classes with no instances are still listed (zero count) so the
    /// UI can show "Animal (0)".
    /// </summary>
    public async Task<ClassesOut?> ListClassesAsync(long ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var counts = _manager.CountsByClass(ksc);
        var entries = classLabels
            .Select(kv => new ClassEntry(
                kv.Key,
                kv.Value,
                counts.TryGetValue(kv.Key, out var n) ? n : 0))
            .ToList();
        // Sort by (-count, label) to match Python abox_classes.
        entries.Sort((a, b) =>
        {
            var cmp = b.Count.CompareTo(a.Count);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Label, b.Label);
        });
        var total = counts.Values.Sum();
        return new ClassesOut(entries, total);
    }

    /// <summary>Paginated individual listing with optional class + q filter.</summary>
    public async Task<IndividualsOut?> ListIndividualsAsync(
        long ksId,
        string? classIri,
        string? q,
        int limit,
        int offset,
        Actor actor,
        CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        if (limit < 1 || limit > 200)
            throw new InvalidOperationException("limit must be between 1 and 200.");
        if (offset < 0)
            throw new InvalidOperationException("offset must be >= 0.");

        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var items = _manager.ListIndividualsPaged(ksc, classLabels, classIri, q, offset, limit);
        var total = _manager.CountIndividualsPaged(ksc, classIri, q);
        return new IndividualsOut(items, total);
    }

    /// <summary>
    /// Single-individual read; returns null when the IRI has no ABox
    /// quads so the dispatcher can map to 404.
    /// </summary>
    public async Task<IndividualOut?> GetIndividualAsync(
        long ksId,
        string iri,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var propLabels = await LoadPropertyLabelsAsync(ks, ct).ConfigureAwait(false);
        return _manager.GetIndividual(ksc, iri, classLabels, propLabels);
    }

    // ----------------------------------------------------------------------
    // Mutations
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new individual in the ABox graph. Writes 3 quads
    /// (rdf:type OwlNamedIndividual, rdf:type <paramref name="req.ClassIri"/>,
    /// rdfs:label) inside a <see cref="StoreWrapper.CaptureAsync"/>
    /// block so a failure mid-flight reverts cleanly. The audit row
    /// carries the N-Quads diff; the <c>AboxProvenanceEntity</c> write
    /// is deferred to the B7b slice (provenance service wire-up).
    /// </summary>
    public async Task<IndividualOut?> CreateIndividualAsync(
        long ksId,
        CreateIndividualRequest req,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        if (string.IsNullOrWhiteSpace(req.Label))
            throw new InvalidOperationException("label is required.");
        if (string.IsNullOrWhiteSpace(req.ClassIri))
            throw new InvalidOperationException("class_iri is required.");

        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        if (!classLabels.ContainsKey(req.ClassIri))
            throw new InvalidOperationException("Unknown class.");

        var ksc = ToKsContext(ks);
        var propLabels = await LoadPropertyLabelsAsync(ks, ct).ConfigureAwait(false);

        var pre = _store.DumpNQuads(ksc.ABoxGraph);
        string iri;
        // QuadChangeCapture semantics: `revertOnError: true` unconditionally
        // rolls the graph back on dispose. To commit the writes on the
        // happy path AND still revert on failure, we open with
        // `revertOnError: false` and call `MarkError()` explicitly when
        // the inner block throws — matching the OntologyEditor pattern.
        await using (var cap = await _store.CaptureAsync(ksc.ABoxGraph, revertOnError: false, waitTimeout: null, ct).ConfigureAwait(false))
        {
            try
            {
                iri = _manager.CreateIndividual(ksc, req.Label, req.ClassIri, req.Label);
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.ABoxGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "abox.add_individual",
            $"Added individual \"{req.Label}\" ({classLabels[req.ClassIri]})",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["class_iri"] = req.ClassIri,
                ["label"] = req.Label,
            },
            ksc.ABoxGraph, added, removed, ct).ConfigureAwait(false);

        return _manager.GetIndividual(ksc, iri, classLabels, propLabels);
    }

    /// <summary>
    /// Delete an individual by IRI. Removes every quad whose subject
    /// matches in the ABox graph, captures the diff, writes the audit
    /// row, and (B7b) tears down the matching provenance rows.
    /// </summary>
    public async Task<DeleteIndividualResponse?> DeleteIndividualAsync(
        long ksId,
        string iri,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var propLabels = await LoadPropertyLabelsAsync(ks, ct).ConfigureAwait(false);
        var existing = _manager.GetIndividual(ksc, iri, classLabels, propLabels);
        if (existing is null)
            throw new InvalidOperationException("Individual not found");

        var pre = _store.DumpNQuads(ksc.ABoxGraph);
        int removed;
        await using (var cap = await _store.CaptureAsync(ksc.ABoxGraph, revertOnError: false, waitTimeout: null, ct).ConfigureAwait(false))
        {
            try
            {
                removed = _manager.DeleteIndividual(ksc, iri);
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.ABoxGraph);
        var (added, removedBytes) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "abox.delete_individual",
            $"Deleted individual \"{existing.Label}\"",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["label"] = existing.Label,
                ["triples_removed"] = removed,
            },
            ksc.ABoxGraph, added, removedBytes, ct).ConfigureAwait(false);

        return new DeleteIndividualResponse(removed);
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> RequireRoleAsync(
        long ksId, Actor actor, KSRole minimum, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
        if (user is null) return (null, null);

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.LegacyId == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null) return (null, null);

        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < minimum) return (null, null);
        return (user, ks);
    }

    /// <summary>
    /// Walk the TBox graph once and collect a <c>class_iri → label</c>
    /// map from every <c>(cls rdf:type owl:Class)</c> /
    /// <c>(cls rdfs:label "...")</c> pair. Mirrors Python's
    /// <c>schema.build_view(...).classes</c> projection.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadClassLabelsAsync(
        KnowledgeSystemEntity ks, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var tboxGraph = new Oxigraph.NamedNode(ks.GraphIri);
        var owlClass = Vocabulary.OwlClass.Value;
        var rdfsLabel = Vocabulary.RdfsLabel.Value;
        // Pull every triple in the TBox once; small graph, single scan is
        // simpler than two match queries.
        var tboxQuads = _store.Match(graph: tboxGraph);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in tboxQuads)
        {
            if (q.Subject is not Oxigraph.NamedNode subj) continue;
            if (q.Predicate.Value == Vocabulary.RdfType.Value
                && q.Object is Oxigraph.NamedNode obj
                && obj.Value == owlClass)
            {
                classes.Add(subj.Value);
            }
            else if (q.Predicate.Value == rdfsLabel
                && q.Object is Oxigraph.Literal lit)
            {
                labels[subj.Value] = lit.Value;
            }
        }
        foreach (var cls in classes)
        {
            map[cls] = labels.TryGetValue(cls, out var l) ? l : ABoxManager.LocalIri(cls);
        }
        await Task.CompletedTask;
        return map;
    }

    /// <summary>
    /// Property-label map (object + data) for the property chips in the
    /// individual envelope. Mirror of the class-label helper.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadPropertyLabelsAsync(
        KnowledgeSystemEntity ks, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var tboxGraph = new Oxigraph.NamedNode(ks.GraphIri);
        var owlObjectProperty = Vocabulary.OwlObjectProperty.Value;
        var owlDatatypeProperty = Vocabulary.OwlDatatypeProperty.Value;
        var rdfsLabel = Vocabulary.RdfsLabel.Value;
        var tboxQuads = _store.Match(graph: tboxGraph);
        var props = new HashSet<string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in tboxQuads)
        {
            if (q.Subject is not Oxigraph.NamedNode subj) continue;
            if (q.Predicate.Value == Vocabulary.RdfType.Value
                && q.Object is Oxigraph.NamedNode obj
                && (obj.Value == owlObjectProperty || obj.Value == owlDatatypeProperty))
            {
                props.Add(subj.Value);
            }
            else if (q.Predicate.Value == rdfsLabel
                && q.Object is Oxigraph.Literal lit)
            {
                labels[subj.Value] = lit.Value;
            }
        }
        foreach (var p in props)
        {
            map[p] = labels.TryGetValue(p, out var l) ? l : ABoxManager.LocalIri(p);
        }
        await Task.CompletedTask;
        return map;
    }

    /// <summary>Project a KS row into the <see cref="KsContext"/> the manager consumes.</summary>
    private static KsContext ToKsContext(KnowledgeSystemEntity ks) => new(
        GraphIri: ks.GraphIri,
        BaseIri: ks.BaseIri);

    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, string summary,
        IReadOnlyDictionary<string, object?> detail,
        string? graph,
        byte[] added, byte[] removed,
        CancellationToken token)
    {
        var nextLegacy = await _db.AuditEvents.AsNoTracking()
            .Select(a => (long?)a.LegacyId)
            .MaxAsync(token)
            .ConfigureAwait(false);
        _db.AuditEvents.Add(new AuditEventEntity
        {
            LegacyId = (nextLegacy ?? 0L) + 1L,
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = summary,
            Detail = JsonDocument.Parse(JsonSerializer.Serialize(detail)),
            Graph = graph,
            Added = added.Length == 0 ? null : added,
            Removed = removed.Length == 0 ? null : removed,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }
}

/// <summary>DI helper for the ABox service registration.</summary>
public static class ABoxServiceCollectionExtensions
{
    public static IServiceCollection AddAboxServices(this IServiceCollection services)
    {
        services.AddScoped<ABoxService>();
        return services;
    }
}