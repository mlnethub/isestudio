using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;

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
    private readonly ABoxProvenanceService _provenance;
    private readonly StoreWrapper _store;
    private readonly ABoxValidator _validator;
    private readonly ValidationDecisionService _decisions;
    private readonly OntologyEditor _editor;

    public ABoxService(
        OnToPilotDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        ABoxManager manager,
        ABoxProvenanceService provenance,
        StoreWrapper store,
        ABoxValidator validator,
        ValidationDecisionService decisions,
        OntologyEditor editor)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _manager = manager;
        _provenance = provenance;
        _store = store;
        _validator = validator;
        _decisions = decisions;
        _editor = editor;
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
    /// Add an object- or data-property assertion to an existing individual.
    /// Mirrors <c>backend/app/api/abox.py::add_assertion</c>: Editor role,
    /// subject must exist, <paramref name="req.Prop"/> must be a declared
    /// TBox property, object-kind assertions require the target IRI to
    /// exist as an ABox individual, and data-kind assertions require a
    /// non-empty <paramref name="req.Value"/>. The mutation is wrapped in
    /// a capture block (revert-on-error), the audit row captures the
    /// N-Quads diff, and the matching <c>AboxProvenanceEntity</c> row is
    /// upserted so the extraction pipeline and the manual-edit pipeline
    /// write to the same canonical-keyed provenance table.
    /// </summary>
    public async Task<IndividualOut?> AddAssertionAsync(
        long ksId,
        AssertionRequest req,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var kind = NormalizeKind(req.Kind);
        var prop = NormalizeProp(req.Prop);
        var (subjectIri, targetIri, value, datatype) = NormalizeAssertionFields(req, kind);

        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var propLabels = await LoadPropertyLabelsAsync(ks, ct).ConfigureAwait(false);

        if (!propLabels.ContainsKey(prop))
            throw new InvalidOperationException("Unknown property.");
        if (!IndividualExists(ksc, subjectIri))
            throw new InvalidOperationException("Subject not found.");
        if (kind == "object" && !IndividualExists(ksc, targetIri!))
            throw new InvalidOperationException("Target individual not found.");

        var factKey = StatementProvenanceService.AssertionKey(subjectIri, prop, kind, targetIri, value);

        var pre = _store.DumpNQuads(ksc.ABoxGraph);
        bool changed;
        await using (var cap = await _store.CaptureAsync(ksc.ABoxGraph, revertOnError: false, waitTimeout: null, ct).ConfigureAwait(false))
        {
            try
            {
                changed = kind == "object"
                    ? _manager.AddObjectAssertion(ksc, subjectIri, prop, targetIri!)
                    : _manager.AddDataAssertion(ksc, subjectIri, prop, value!, datatype);
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.ABoxGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        var audit = await WriteAuditAsync(ks.Id, user, "abox.add_assertion",
            BuildAssertionSummary(req, propLabels, subjectIri, "Added"),
            BuildAssertionDetail(req, factKey),
            ksc.ABoxGraph, added, removed, ct).ConfigureAwait(false);

        if (changed)
        {
            await _provenance.RecordFactAsync(
                ks.Id, factKey, audit.Id,
                method: "manual",
                actorName: audit.ActorName,
                ct).ConfigureAwait(false);
        }

        return _manager.GetIndividual(ksc, subjectIri, classLabels, propLabels);
    }

    /// <summary>
    /// Remove an object- or data-property assertion. Mirrors the Python
    /// <c>abox.remove_assertion</c> path: Editor role, same validations as
    /// add, capture + revert, audit <c>abox.remove_assertion</c>, and
    /// delete the matching <c>AboxProvenanceEntity</c> row so the provenance
    /// surface stays in lock-step with the RDF graph.
    /// </summary>
    public async Task<IndividualOut?> RemoveAssertionAsync(
        long ksId,
        AssertionRequest req,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var kind = NormalizeKind(req.Kind);
        var prop = NormalizeProp(req.Prop);
        var (subjectIri, targetIri, value, datatype) = NormalizeAssertionFields(req, kind);

        var ksc = ToKsContext(ks);
        var classLabels = await LoadClassLabelsAsync(ks, ct).ConfigureAwait(false);
        var propLabels = await LoadPropertyLabelsAsync(ks, ct).ConfigureAwait(false);

        if (!IndividualExists(ksc, subjectIri))
            throw new InvalidOperationException("Subject not found.");
        if (kind == "object" && string.IsNullOrEmpty(targetIri))
            throw new InvalidOperationException("Target individual is required.");

        var factKey = StatementProvenanceService.AssertionKey(subjectIri, prop, kind, targetIri, value);

        var pre = _store.DumpNQuads(ksc.ABoxGraph);
        await using (var cap = await _store.CaptureAsync(ksc.ABoxGraph, revertOnError: false, waitTimeout: null, ct).ConfigureAwait(false))
        {
            try
            {
                if (kind == "object") _manager.RemoveObjectAssertion(ksc, subjectIri, prop, targetIri!);
                else _manager.RemoveDataAssertion(ksc, subjectIri, prop, value!, datatype);
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.ABoxGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "abox.remove_assertion",
            BuildAssertionSummary(req, propLabels, subjectIri, "Removed"),
            BuildAssertionDetail(req, factKey),
            ksc.ABoxGraph, added, removed, ct).ConfigureAwait(false);

        // Provenance delete is idempotent: a no-op RDF remove just leaves
        // the row count at zero. Keeps manual-edit provenance in lock-step
        // with the RDF graph without a pre-check.
        await _provenance.RemoveFactsAsync(ks.Id, factKey, ct).ConfigureAwait(false);

        return _manager.GetIndividual(ksc, subjectIri, classLabels, propLabels);
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

    private async Task<AuditEventEntity> WriteAuditAsync(
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
        var entity = new AuditEventEntity
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
        };
        _db.AuditEvents.Add(entity);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return entity;
    }

    // ----------------------------------------------------------------------
    // Assertion helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Normalise the <c>kind</c> field — accepts <c>"object"</c> / <c>"data"</c>
    /// in any case (Python sends <c>"object"</c> / <c>"data"</c> verbatim),
    /// rejects everything else with a 400-friendly message.
    /// </summary>
    private static string NormalizeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("kind is required.");
        var k = raw.Trim().ToLowerInvariant();
        return k switch
        {
            "object" => "object",
            "data" => "data",
            _ => throw new InvalidOperationException("kind must be 'object' or 'data'."),
        };
    }

    /// <summary>Trim and require a non-empty property IRI.</summary>
    private static string NormalizeProp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("prop is required.");
        return raw.Trim();
    }

    /// <summary>
    /// Pull the request body into a uniform tuple the add/remove paths
    /// share: <c>(subject, target?, value?, datatype?)</c> after validating
    /// that the kind-specific fields are present (object → target,
    /// data → value).
    /// </summary>
    private static (string Subject, string? Target, string? Value, string? Datatype)
        NormalizeAssertionFields(AssertionRequest req, string kind)
    {
        var subject = (req.Subject ?? string.Empty).Trim();
        if (subject.Length == 0)
            throw new InvalidOperationException("subject is required.");
        if (kind == "object")
        {
            var target = (req.Target ?? string.Empty).Trim();
            if (target.Length == 0)
                throw new InvalidOperationException("Object assertion requires a target.");
            return (subject, target, null, null);
        }
        var value = req.Value;
        if (value is null)
            throw new InvalidOperationException("Data assertion requires a value.");
        return (subject, null, value, string.IsNullOrWhiteSpace(req.Datatype) ? null : req.Datatype.Trim());
    }

    /// <summary>
    /// Cheap "does this IRI have any ABox quads?" check — wraps
    /// <see cref="StoreWrapper.Match(string, string, string, string, string?)"/>
    /// with just the subject + graph predicate. Avoids the cost of pulling
    /// the full <see cref="IndividualOut"/> envelope on the validation path.
    /// </summary>
    private bool IndividualExists(KsContext ksc, string iri) =>
        _store.Match(subjectIri: iri, graphIri: ksc.ABoxGraph).Count > 0;

    /// <summary>
    /// Human-readable audit summary for an assertion operation. Mirrors
    /// the Python <c>_assert_summary(a, prop_labels, subj_label, verb)</c>
    /// helper &mdash; object kind renders <c>"&lt;verb&gt; "&lt;label&gt;"
    /// —&lt;prop&gt;&rarr; (individual)"</c>, data kind renders
    /// <c>"&lt;verb&gt; &lt;prop&gt; = "&lt;value&gt;" on "&lt;label&gt;"</c>.
    /// </summary>
    private static string BuildAssertionSummary(
        AssertionRequest req,
        IReadOnlyDictionary<string, string> propLabels,
        string subjectIri,
        string verb)
    {
        var propLabel = propLabels.TryGetValue(req.Prop, out var pl)
            ? pl
            : ABoxManager.LocalIri(req.Prop);
        if (string.Equals(req.Kind, "object", StringComparison.OrdinalIgnoreCase))
            return $"{verb} \"{subjectIri}\" —{propLabel}→ (individual)";
        return $"{verb} {propLabel} = \"{req.Value}\" on \"{subjectIri}\"";
    }

    /// <summary>
    /// Detail JSON for the audit row. Includes the fact key so history
    /// replay / roll-back can identify the canonical provenance key the
    /// assertion was filed under.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildAssertionDetail(
        AssertionRequest req, string factKey)
    {
        return new Dictionary<string, object?>
        {
            ["subject"] = req.Subject,
            ["prop"] = req.Prop,
            ["kind"] = req.Kind,
            ["target"] = req.Target,
            ["value"] = req.Value,
            ["datatype"] = req.Datatype,
            ["fact_key"] = factKey,
        };
    }

    // ----------------------------------------------------------------------
    // B7c — reset / validate / fix_violation / validation decisions
    // ----------------------------------------------------------------------

    /// <summary>
    /// Wipe the ABox graph + provenance + resolution rows for one KS.
    /// Mirrors Python <c>backend/app/api/abox.py::reset_abox</c>: the
    /// <c>confirm</c> guard rejects a UI typo, the extraction guard
    /// blocks a reset during a running extraction (wired by the
    /// dispatcher arm via <see cref="InternalOperationDispatcher.RunWithExtractionGuardAsync"/>),
    /// and the audit row carries the removed-quads byte[] so history
    /// replay can roll back the wipe.
    /// </summary>
    public async Task<ResetAboxResponse?> ResetAsync(
        long ksId, ResetAboxRequest req, Actor actor, CancellationToken ct)
    {
        if (!req.Confirm)
        {
            throw new InvalidOperationException(
                "confirm=true is required to reset all instances");
        }
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var provenanceRows = await _db.AboxProvenances.AsNoTracking()
            .CountAsync(p => p.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        var resolutionRows = await _db.EntityResolutions.AsNoTracking()
            .CountAsync(r => r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);

        var aboxGraph = new OntoNamedNode(ks.GraphIri.TrimEnd('/') + "/abox");
        var preBytes = _store.DumpNQuads(aboxGraph);
        await using (var cap = await _store
            .CaptureAsync(ks.GraphIri.TrimEnd('/') + "/abox", revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                _store.ReplaceGraph(aboxGraph, Array.Empty<OntoQuad>());
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var postBytes = _store.DumpNQuads(aboxGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(preBytes, postBytes);

        // SQL cleanup mirrors Python: drop AboxProvenance + EntityResolution
        // rows for this KS so a fresh extraction starts from a blank slate.
        var provenanceDeletes = await _db.AboxProvenances
            .Where(p => p.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _db.AboxProvenances.RemoveRange(provenanceDeletes);
        var resolutionDeletes = await _db.EntityResolutions
            .Where(r => r.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _db.EntityResolutions.RemoveRange(resolutionDeletes);

        await WriteAuditAsync(ks.Id, user!, "abox.reset",
            "Reset all instances for re-extraction",
            new Dictionary<string, object?>
            {
                    ["provenance_rows"] = provenanceRows,
                    ["resolution_rows"] = resolutionRows,
            },
            aboxGraph.Value, added, removed, ct).ConfigureAwait(false);

        return new ResetAboxResponse(removed.Length, provenanceRows, resolutionRows);
    }

    /// <summary>
    /// Run <see cref="ABoxValidator"/> against the KS and return the
    /// wire-shaped report. Read-side only &mdash; no extraction guard.
    /// </summary>
    public async Task<ValidationReportOut?> ValidateAsync(
        long ksId, Actor actor, CancellationToken ct)
    {
        var (_, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var ksc = ToKsContext(ks);
        var report = _validator.Validate(ksc);
        return MapReport(report);
    }

    /// <summary>
    /// Apply one fix op. Mirrors Python
    /// <c>backend/app/api/abox.py::fix_violation</c>:
    /// <list type="bullet">
    /// <item><c>delete_individual</c> &mdash; ABox <see cref="ABoxManager.DeleteIndividual"/></item>
    /// <item><c>remove_type</c> &mdash; <see cref="ABoxManager.RemoveType"/></item>
    /// <item><c>remove_object_assertion</c> &mdash;
    ///   <see cref="ABoxManager.RemoveObjectAssertion"/></item>
    /// <item><c>remove_data_assertion</c> &mdash;
    ///   <see cref="ABoxManager.RemoveDataAssertion"/></item>
    /// <item><c>relax_range</c> &mdash; schema edit on the TBox graph
    ///   (<see cref="OntologyEditor.UpdateProperty"/> with
    ///   <c>range: "string"</c>); also records a validation decision
    ///   so a future <c>ValidationAgent</c> reuses the same preference.</item>
    /// </list>
    /// Returns a fresh validation report so the UI can re-render the
    /// violation list with the fix applied.
    /// </summary>
    public async Task<ValidationReportOut?> FixViolationAsync(
        long ksId, FixViolationRequest req, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var op = req.Op ?? new Dictionary<string, JsonElement>();
        if (!op.TryGetValue("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Fix op requires a 'kind' field.");
        }
        var kind = kindEl.GetString()!;

        byte[] added, removed;
        string graphIri;
        var ksc = ToKsContext(ks);

        if (kind == "relax_range")
        {
            // Schema edit on the TBox graph, not the ABox graph.
            // NOTE: do NOT wrap this in our own CaptureAsync —
            // OntologyEditor.ApplyEditAsync opens its own capture on the
            // same graphIri, which would trip
            // GraphWriteCoordinator's LockRecursionException → 409 path.
            // The inner capture owns the revert-or-commit semantics; we
            // only need pre/post dumps around the call for the audit
            // diff.
            var propIri = RequireString(op, "prop");
            var propLabel = op.AsString("prop_label") ?? propIri;
            var xsd = op.AsString("xsd");
            graphIri = ks.GraphIri;
            var tboxGraph = new OntoNamedNode(graphIri);
            var preBytes = _store.DumpNQuads(tboxGraph);
            _editor.ApplyEditAsync(graphIri, ksc.BaseIri,
                new Dictionary<string, object?>
                {
                    ["op"] = "update_property",
                    ["iri"] = propIri,
                    ["range"] = "string",
                }, ct).GetAwaiter().GetResult();
            var postBytes = _store.DumpNQuads(tboxGraph);
            (added, removed) = StoreWrapper.DiffNQuads(preBytes, postBytes);

            // Remember the human's preference so the future agent
            // doesn't re-judge this property next triage.
            await _decisions.RecordDecisionAsync(
                ks.Id, propIri, propLabel, xsd,
                "relax", "human relaxed the range to text",
                user!.DisplayName ?? user.Username, ct).ConfigureAwait(false);
        }
        else
        {
            graphIri = ksc.ABoxGraph;
            var aboxGraph = new OntoNamedNode(graphIri);
            var preBytes = _store.DumpNQuads(aboxGraph);
            await using (var cap = await _store
                .CaptureAsync(graphIri, revertOnError: false, waitTimeout: null, ct)
                .ConfigureAwait(false))
            {
                try
                {
                    ApplyFixOp(ksc, kind, op);
                }
                catch
                {
                    cap.MarkError();
                    throw;
                }
            }
            var postBytes = _store.DumpNQuads(aboxGraph);
            (added, removed) = StoreWrapper.DiffNQuads(preBytes, postBytes);
        }

        await WriteAuditAsync(ks.Id, user, "abox.fix_violation",
            req.Summary ?? $"Fixed instance violation ({kind})",
            JsonElementDictToObject(op),
            graphIri, added, removed, ct).ConfigureAwait(false);

        var report = _validator.Validate(ksc);
        return MapReport(report);
    }

    /// <summary>List persisted validation decisions for one KS.</summary>
    public async Task<ValidationDecisionListOut?> ListValidationDecisionsAsync(
        long ksId, string? q, int limit, int offset, Actor actor, CancellationToken ct)
    {
        var (_, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (ks is null) return null;
        return await _decisions
            .ListDecisionsAsync(ks.Id, q, limit, offset, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forget one decision row. Returns the revoked id, or <c>null</c>
    /// when no row matched (the caller can map null → 404).
    /// </summary>
    public async Task<RevokeValidationDecisionResponse?> RevokeValidationDecisionAsync(
        long ksId, Guid decisionId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var revoked = await _decisions.RevokeAsync(ks.Id, decisionId, ct).ConfigureAwait(false);
        if (revoked is null) return null;
        await WriteAuditAsync(ks.Id, user!, "validation.revoke",
            "Forgot validation memory",
            new Dictionary<string, object?> { ["decision_id"] = revoked },
            null, Array.Empty<byte>(), Array.Empty<byte>(), ct).ConfigureAwait(false);
        return new RevokeValidationDecisionResponse(revoked.Value);
    }

    // ----------------------------------------------------------------------
    // B7c helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Dispatch a single ABox fix op (everything except
    /// <c>relax_range</c>, which lands on the TBox graph). Mirrors the
    /// Python <c>backend/app/ontology/abox_validate.py::apply_fix</c>
    /// dispatch table.
    /// </summary>
    private void ApplyFixOp(KsContext ks, string kind, Dictionary<string, JsonElement> op)
    {
        switch (kind)
        {
            case "delete_individual":
                _manager.DeleteIndividual(ks, RequireString(op, "iri"));
                return;
            case "remove_type":
                _manager.RemoveType(ks, RequireString(op, "iri"), RequireString(op, "class_iri"));
                return;
            case "remove_object_assertion":
                _manager.RemoveObjectAssertion(ks,
                    RequireString(op, "subject"),
                    RequireString(op, "prop"),
                    RequireString(op, "target"));
                return;
            case "remove_data_assertion":
            {
                var subject = RequireString(op, "subject");
                var prop = RequireString(op, "prop");
                var value = RequireString(op, "value");
                var datatype = op.AsString("datatype");
                _manager.RemoveDataAssertion(ks, subject, prop, value, datatype);
                return;
            }
            default:
                throw new InvalidOperationException($"Unknown fix op kind: {kind}");
        }
    }

    /// <summary>Pull a required string field out of a fix op, or throw a 400-friendly error.</summary>
    private static string RequireString(Dictionary<string, JsonElement> op, string key)
    {
        var s = op.AsString(key);
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new InvalidOperationException($"Fix op requires a non-empty '{key}' field.");
        }
        return s!;
    }

    /// <summary>
    /// Convert a <c>Dictionary&lt;string, JsonElement&gt;</c> fix-op into
    /// the <c>Dictionary&lt;string, object?&gt;</c> shape the audit row
    /// expects. JsonElement values are unwrapped to their underlying
    /// primitive (<see cref="string"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="bool"/>, <c>null</c>) so the
    /// audit row stays JSON-friendly.
    /// </summary>
    private static Dictionary<string, object?> JsonElementDictToObject(
        Dictionary<string, JsonElement> op)
    {
        var result = new Dictionary<string, object?>(op.Count);
        foreach (var (k, el) in op)
        {
            result[k] = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? (object?)l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => el.GetRawText(),
            };
        }
        return result;
    }

    /// <summary>Map the validator's internal report to the wire DTO.</summary>
    private static ValidationReportOut MapReport(ABoxValidationReport report)
    {
        var violations = report.Violations
            .Select(v => new ValidationViolationOut(
                v.Id, v.Type, v.Severity, v.Individual, v.Summary,
                v.Fixes.Select(f => new ViolationFixOut(f.Id, f.Label, f.Op)).ToList()))
            .ToList();
        return new ValidationReportOut(
            violations,
            new ValidationReportCounts(report.ErrorCount, report.WarningCount),
            report.Truncated);
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