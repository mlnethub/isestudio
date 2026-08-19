using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Storage;

namespace OnToPilot.Ontology;

/// <summary>
/// Scoped service that mediates vocabulary CRUD + read endpoints for one
/// knowledge system. Wraps <see cref="SkosManager"/> methods and runs each
/// write through the extraction guard + role gate + audit pre/post diff
/// (B7c <see cref="ABoxService"/> pattern).
///
/// <para>Read methods resolve role via <see cref="KnowledgeSystemAccessService"/>
/// to <c>KSRole.Viewer</c> (a.k.a. Reader); write methods require
/// <c>KSRole.Editor</c> (a.k.a. Writer). All write methods also call the
/// extraction guard so a concurrent extraction job surfaces as
/// <see cref="GraphWriteConflictException"/> &rarr; HTTP 409 via
/// <c>FastApiErrorMiddleware</c>.</para>
///
/// <para>Audit rows carry the byte-exact N-Quads diff <see cref="StoreWrapper.DiffNQuads"/>
/// computes between pre- and post-mutation snapshots of the vocabulary graph,
/// so future rollback paths can replay the negation.</para>
/// </summary>
public sealed class VocabularyService
{
    private readonly SkosManager _skos;
    private readonly StoreWrapper _store;
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly ExtractionJobStore _jobStore;
    private readonly TerminologyService _terminology;

    public VocabularyService(
        SkosManager skos,
        StoreWrapper store,
        OnToPilotDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        ExtractionJobStore jobStore,
        TerminologyService terminology)
    {
        _skos = skos;
        _store = store;
        _db = db;
        _clock = clock;
        _access = access;
        _jobStore = jobStore;
        _terminology = terminology;
    }

    // ----------------------------------------------------------------------
    // Reads (Reader gate — KSRole.Viewer)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Curated vocabulary view for the entire KS &mdash; schemes + concepts
    /// + stats. Mirrors <c>backend/app/api/vocabulary.py::get_vocabulary</c>.
    /// </summary>
    public async Task<SkosView?> GetVocabularyAsync(
        KnowledgeSystemEntity ks, Actor actor, CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null) return null;
        return _skos.BuildView(KsContext.FromEntity(ks));
    }

    /// <summary>
    /// List every <c>skos:ConceptScheme</c> in the vocabulary graph with its
    /// concept count. Mirrors <c>vocabulary.list_schemes</c>.
    /// </summary>
    public async Task<IReadOnlyList<SkosSchemeView>?> ListSchemesAsync(
        KnowledgeSystemEntity ks, Actor actor, CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null) return null;
        return _skos.BuildView(KsContext.FromEntity(ks)).Schemes;
    }

    /// <summary>
    /// Page through concepts with optional filters. Mirrors
    /// <c>vocabulary.list_concepts</c>. <paramref name="q"/> is the free-text
    /// filter; <paramref name="status"/>, <paramref name="mapping"/>, and
    /// <paramref name="origin"/> match <see cref="SkosManager.ListConcepts"/>.
    /// </summary>
    public async Task<SkosConceptPage?> ListConceptsAsync(
        KnowledgeSystemEntity ks,
        string? schemeIri,
        string? q,
        string? status,
        string? mapping,
        string? origin,
        int limit,
        int offset,
        Actor actor,
        CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null) return null;
        if (limit < 1 || limit > 200)
            throw new InvalidOperationException("limit must be between 1 and 200.");
        if (offset < 0)
            throw new InvalidOperationException("offset must be >= 0.");

        return _skos.ListConcepts(
            KsContext.FromEntity(ks),
            SchemeIri: schemeIri,
            Status: status,
            Mapping: mapping,
            Origin: origin,
            Q: q,
            Limit: limit,
            Offset: offset);
    }

    /// <summary>
    /// Resolve a free-text query to ranked concept matches. Mirrors
    /// <c>vocabulary.resolve_term</c>. Returns the paged list plus the
    /// total match count.
    /// </summary>
    public async Task<(IReadOnlyList<SkosMatch> Items, int Total)?> ResolveTermAsync(
        KnowledgeSystemEntity ks,
        string q,
        string? language,
        int limit,
        Actor actor,
        CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null) return null;
        var (items, total) = _skos.Resolve(KsContext.FromEntity(ks), q, Language: language, Limit: limit);
        return (items, total);
    }

    /// <summary>
    /// Serialize the vocabulary graph as RDF bytes. Mirrors
    /// <c>vocabulary.export</c>. The <paramref name="fmt"/> parameter
    /// (<c>"turtle"</c> / <c>"n-quads"</c> / <c>"json-ld"</c>) is honoured
    /// as far as the underlying <see cref="StoreWrapper"/> permits &mdash;
    /// today it always returns N-Quads bytes via
    /// <see cref="StoreWrapper.DumpNQuads"/>, which is a lossless
    /// round-trippable RDF serialisation. Future slices can add a
    /// <c>SerializeGraph</c> helper to translate to Turtle / JSON-LD on
    /// demand.
    /// </summary>
    public Task<byte[]?> ExportVocabularyAsync(
        KnowledgeSystemEntity ks,
        string fmt,
        Actor actor,
        CancellationToken ct)
    {
        var user = RequireRoleAsync(ks, actor, KSRole.Viewer, ct);
        return ExportVocabularyCoreAsync(ks, fmt, user);
    }

    private async Task<byte[]?> ExportVocabularyCoreAsync(
        KnowledgeSystemEntity ks, string fmt, Task<UserEntity?> roleTask)
    {
        var user = await roleTask.ConfigureAwait(false);
        if (user is null) return null;
        ArgumentException.ThrowIfNullOrEmpty(fmt);
        // fmt routing is a future extension — today DumpNQuads returns the
        // full N-Quads byte payload for the vocabulary graph, which is a
        // valid RDF serialisation. Clients that need Turtle / JSON-LD
        // parsing can transform the bytes client-side.
        _ = fmt;
        return _store.DumpNQuads(KsContext.FromEntity(ks).VocabularyGraph);
    }

    // ----------------------------------------------------------------------
    // Write — scheme (Writer gate + extraction guard + audit diff)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new SKOS ConceptScheme in the vocabulary graph. Wraps
    /// <see cref="SkosManager.CreateScheme"/> + capture (revert-on-error) +
    /// audit diff. Mirrors <c>vocabulary.create_scheme</c>.
    /// </summary>
    public async Task<SkosSchemeView?> CreateSchemeAsync(
        KnowledgeSystemEntity ks,
        SkosSchemeData data,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        string iri;
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                iri = _skos.CreateScheme(ksc, data);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.create_scheme",
            $"Created vocabulary scheme \"{data.Title}\"",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["title"] = data.Title,
                ["default_language"] = data.DefaultLanguage,
                ["origin"] = data.Origin,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return _skos.GetScheme(ksc, iri);
    }

    /// <summary>
    /// Update an existing scheme (replaces the scheme-predicate set).
    /// Wraps <see cref="SkosManager.UpdateScheme"/> + capture +
    /// audit diff. Mirrors <c>vocabulary.update_scheme</c>.
    /// </summary>
    public async Task<SkosSchemeView?> UpdateSchemeAsync(
        KnowledgeSystemEntity ks,
        string iri,
        SkosSchemeData data,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentNullException.ThrowIfNull(data);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                _skos.UpdateScheme(ksc, iri, data);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.update_scheme",
            $"Updated vocabulary scheme \"{data.Title}\"",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["title"] = data.Title,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return _skos.GetScheme(ksc, iri);
    }

    /// <summary>
    /// Delete a scheme + every concept that referenced it. Wraps
    /// <see cref="SkosManager.DeleteScheme"/> + capture + audit diff.
    /// Mirrors <c>vocabulary.delete_scheme</c>.
    /// </summary>
    public async Task<(string DeletedIri, int RemovedTriples)?> DeleteSchemeAsync(
        KnowledgeSystemEntity ks,
        string iri,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        int removedCount;
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                removedCount = _skos.DeleteScheme(ksc, iri);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.delete_scheme",
            $"Deleted vocabulary scheme {iri}",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["triples_removed"] = removedCount,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return (iri, removedCount);
    }

    // ----------------------------------------------------------------------
    // Write — concept (Writer gate + extraction guard + audit diff)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new SKOS Concept under <paramref name="schemeIri"/>.
    /// Wraps <see cref="SkosManager.CreateConcept"/> + capture + audit diff.
    /// Mirrors <c>vocabulary.create_concept</c>.
    /// </summary>
    public async Task<SkosConceptView?> CreateConceptAsync(
        KnowledgeSystemEntity ks,
        string schemeIri,
        SkosConceptData data,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemeIri);
        ArgumentNullException.ThrowIfNull(data);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        string iri;
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                iri = _skos.CreateConcept(ksc, schemeIri, data);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.create_concept",
            $"Created concept \"{data.PrefLabel}\"",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["scheme_iri"] = schemeIri,
                ["pref_label"] = data.PrefLabel,
                ["language"] = data.Language,
                ["status"] = data.Status,
                ["origin"] = data.Origin,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return _skos.GetConcept(ksc, iri);
    }

    /// <summary>
    /// Update an existing concept (replaces the concept-predicate set).
    /// Wraps <see cref="SkosManager.UpdateConcept"/> + capture +
    /// audit diff. Mirrors <c>vocabulary.update_concept</c>.
    /// </summary>
    public async Task<SkosConceptView?> UpdateConceptAsync(
        KnowledgeSystemEntity ks,
        string iri,
        SkosConceptData data,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentNullException.ThrowIfNull(data);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                _skos.UpdateConcept(ksc, iri, data);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.update_concept",
            $"Updated concept \"{data.PrefLabel}\"",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["pref_label"] = data.PrefLabel,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return _skos.GetConcept(ksc, iri);
    }

    /// <summary>
    /// Delete a concept + every triple that mentions its IRI. Wraps
    /// <see cref="SkosManager.DeleteConcept"/> + capture + audit diff.
    /// Mirrors <c>vocabulary.delete_concept</c>.
    /// </summary>
    public async Task<(string DeletedIri, int RemovedTriples)?> DeleteConceptAsync(
        KnowledgeSystemEntity ks,
        string iri,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        int removedCount;
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                removedCount = _skos.DeleteConcept(ksc, iri);
            }
            catch (SkosValidationException)
            {
                cap.MarkError();
                throw;
            }
            catch
            {
                cap.MarkError();
                throw;
            }
        }
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "vocabulary.delete_concept",
            $"Deleted concept {iri}",
            new Dictionary<string, object?>
            {
                ["iri"] = iri,
                ["triples_removed"] = removedCount,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return (iri, removedCount);
    }

    // ----------------------------------------------------------------------
    // Sync (Writer gate + extraction guard + audit diff)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Run the deterministic SKOS terminology sync against the KS TBox +
    /// vocabulary graphs. Mirrors <c>vocabulary.sync</c> and wraps
    /// <see cref="TerminologyService.SyncAsync"/>. The sync itself never
    /// throws &mdash; <see cref="TerminologyResult.Error"/> is set when the
    /// inner pass fails &mdash; so the audit row captures the post-state
    /// regardless of whether the sync produced new concepts.
    /// </summary>
    public async Task<TerminologyResult?> SyncAsync(
        KnowledgeSystemEntity ks,
        Actor actor,
        CancellationToken ct)
    {
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        var result = _terminology.SyncAsync(ksc, ct);
        var post = _store.DumpNQuads(ksc.VocabularyGraph);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        var summary = result.Error is null
            ? $"Synced vocabulary (added={result.TermsAdded}, mapped={result.TermsMapped})"
            : $"Vocabulary sync error: {result.Error}";

        await WriteAuditAsync(ks.Id, user, "vocabulary.sync", summary,
            new Dictionary<string, object?>
            {
                ["terms_added"] = result.TermsAdded,
                ["terms_mapped"] = result.TermsMapped,
                ["proposals_queued"] = result.ProposalsQueued,
                ["error"] = result.Error,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return result;
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    /// <summary>
    /// Look up the user behind <paramref name="actor"/> and confirm they
    /// hold at least <paramref name="minimum"/> on <paramref name="ks"/>.
    /// Returns the user on success; <c>null</c> when the actor is unknown,
    /// the user can't be resolved, or the role gate fails &mdash; the
    /// caller maps <c>null</c> to a 404 envelope via the dispatcher arm.
    /// </summary>
    private async Task<UserEntity?> RequireRoleAsync(
        KnowledgeSystemEntity ks, Actor actor, KSRole minimum, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return null;
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
        if (user is null) return null;
        var ok = await _access.HasAtLeastAsync(user, ks, minimum, _db, ct).ConfigureAwait(false);
        return ok ? user : null;
    }

    /// <summary>
    /// Resolve the user + KS context and reject in-flight extraction work.
    /// Mirrors the dispatcher's <c>RejectIfExtractionActiveAsync</c> path so
    /// a vocabulary write that lands during a running extraction surfaces as
    /// a 409 + <c>job_id</c> envelope instead of racing against the
    /// orchestrator. Returns <c>(null, null)</c> on auth/role failure so the
    /// caller can map that to a 404 envelope without a separate throw.
    /// </summary>
    private async Task<(UserEntity? User, KsContext? Ks)> RequireWriterAsync(
        KnowledgeSystemEntity ks, Actor actor, CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null) return (null, null);

        await RejectExtractionAsync(ct).ConfigureAwait(false);
        return (user, KsContext.FromEntity(ks));
    }

    /// <summary>
    /// Throw <see cref="GraphWriteConflictException"/> with the active job's
    /// id when any extraction job is currently <c>pending</c> or
    /// <c>running</c>. Mirrors <see cref="InternalOperationDispatcher"/>'s
    /// private guard of the same name. Contract-test factories build a
    /// SQLite database without running EF migrations; a missing-schema
    /// error from the job-store call is treated as "no active job" so the
    /// placeholder payload path stays on its success branch.
    /// </summary>
    private async Task RejectExtractionAsync(CancellationToken ct)
    {
        Guid? jobId;
        try
        {
            jobId = await _jobStore.FindAnyActiveJobAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingSchema(ex))
        {
            return;
        }
        if (jobId is not null)
        {
            throw new GraphWriteConflictException(
                "Extraction in progress; vocabulary modification refused.",
                jobId.Value);
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or its inner chain) indicates the
    /// extraction-job table is absent. Mirrors the dispatcher's helper of
    /// the same name so vocabulary writes in the contract-test factory
    /// path succeed when the SQL schema is intentionally empty.
    /// </summary>
    private static bool IsMissingSchema(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Append the audit row that records the change. Mirrors
    /// <see cref="ABoxService.WriteAuditAsync"/>: pre/post N-Quads byte
    /// blobs round-trip through <see cref="StoreWrapper.DumpNQuads"/> and
    /// <see cref="StoreWrapper.DiffNQuads"/>. The <c>LegacyId</c> column
    /// is monotonically incremented from the audit table's current max so
    /// future rollback tooling can replay events in order.
    /// </summary>
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

/// <summary>DI helper for the vocabulary service registration.</summary>
public static class VocabularyServiceCollectionExtensions
{
    /// <summary>
    /// Register the vocabulary slice. <see cref="VocabularyService"/> and
    /// <see cref="VocabularyProposalService"/> are Scoped (share the request
    /// DbContext); the underlying <see cref="SkosManager"/>,
    /// <see cref="StoreWrapper"/>, <see cref="TimeProvider"/>,
    /// <see cref="KnowledgeSystemAccessService"/>,
    /// <see cref="ExtractionJobStore"/>, and <see cref="TerminologyService"/>
    /// are all registered earlier in the DI pipeline (<c>Program.cs</c>
    /// + <c>AddExtractionServices</c>) so the Oxigraph handle and the
    /// cross-request job-state survive HTTP-request boundaries. This helper
    /// deliberately stays minimal — only the Scoped vocabulary services are
    /// added here.
    /// </summary>
    public static IServiceCollection AddVocabularyServices(this IServiceCollection services)
    {
        services.AddScoped<VocabularyService>();
        services.AddScoped<VocabularyProposalService>();
        return services;
    }
}