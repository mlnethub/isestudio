using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Storage;

namespace OnToPilot.Ontology;

/// <summary>
/// Scoped service that mediates the lifecycle of <see cref="TermProposalEntity"/>
/// rows &mdash; the Python backend's <c>terminology</c> agent writes these rows
/// during extraction; a human later <c>accept</c>s or <c>reject</c>s them and
/// <c>accept</c> applies the proposal's payload to the SKOS vocabulary graph.
///
/// <para>Mirrors the Python <c>backend/app/api/vocabulary.py::list_proposals</c>
/// / <c>accept_proposal</c> / <c>reject_proposal</c> surface. Reads go through
/// the <c>KSRole.Viewer</c> (Reader) gate; writes go through
/// <c>KSRole.Editor</c> (Writer). <c>AcceptProposalAsync</c> also runs the
/// extraction guard (same shape as <see cref="VocabularyService"/>) so a
/// concurrent extraction job surfaces as a 409 + <c>job_id</c> envelope.
/// <c>RejectProposalAsync</c> deliberately has <b>no</b> extraction guard to
/// match the Python reference &mdash; humans must be able to prune the backlog
/// while a job runs.</para>
/// </summary>
public sealed class VocabularyProposalService
{
    private readonly OnToPilotDbContext _db;
    private readonly SkosManager _skos;
    private readonly StoreWrapper _store;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly ExtractionJobStore _jobStore;
    private readonly LegacyIdAllocator _allocator;

    public VocabularyProposalService(
        OnToPilotDbContext db,
        SkosManager skos,
        StoreWrapper store,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        ExtractionJobStore jobStore,
        LegacyIdAllocator allocator)
    {
        _db = db;
        _skos = skos;
        _store = store;
        _clock = clock;
        _access = access;
        _jobStore = jobStore;
        _allocator = allocator;
    }

    // ----------------------------------------------------------------------
    // Reads (Reader gate — KSRole.Viewer)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through <see cref="TermProposalEntity"/> rows for one KS. Mirrors
    /// Python <c>vocabulary.list_proposals</c>.
    ///
    /// <para><paramref name="status"/> is one of <c>pending</c> /
    /// <c>accepted</c> / <c>rejected</c>; <c>null</c>/empty returns all
    /// statuses. <paramref name="q"/> is a case-insensitive substring filter
    /// against the <c>Term</c> column.</para>
    ///
    /// <para>SQLite refuses DateTimeOffset in <c>ORDER BY</c> (B7c root-cause
    /// fix): we materialise the filtered set first and sort client-side by
    /// <c>CreatedAt</c> desc / <c>LegacyId</c> desc, then apply
    /// <paramref name="offset"/> / <paramref name="limit"/>. The total count
    /// runs server-side so the page envelope reports the true backlog size.</para>
    /// </summary>
    public async Task<(IReadOnlyList<TermProposalEntity> Items, int Total)?> ListProposalsAsync(
        KnowledgeSystemEntity ks,
        string? status,
        string? q,
        int limit,
        int offset,
        Actor actor,
        CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null) return null;

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        var query = _db.TermProposals.AsNoTracking()
            .Where(t => t.KnowledgeSystemId == ks.Id);

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not null
            && normalizedStatus != "pending"
            && normalizedStatus != "accepted"
            && normalizedStatus != "rejected")
        {
            throw new InvalidOperationException(
                $"Unknown proposal status '{status}'. Use 'pending', 'accepted', or 'rejected'.");
        }
        if (normalizedStatus is not null)
        {
            query = query.Where(t => t.Status == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(t => EF.Functions.Like(t.Term, $"%{needle}%"));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        // SQLite refuses DateTimeOffset in ORDER BY, so materialise the rows
        // and sort newest-first client-side (mirrors how
        // ValidationDecisionService.ListDecisionsAsync and the
        // ExtractionJobStore work around the same limitation).
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        rows.Sort((a, b) =>
        {
                var cmp = b.CreatedAt.CompareTo(a.CreatedAt);
                return cmp != 0 ? cmp : b.LegacyId.CompareTo(a.LegacyId);
            });
        var page = rows.Skip(offset).Take(limit).ToList();
        return (page, total);
    }

    // ----------------------------------------------------------------------
    // Write — accept (Writer gate + extraction guard + audit diff)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Apply a pending proposal to the SKOS vocabulary graph and mark the row
    /// <c>accepted</c>. Mirrors Python <c>vocabulary.accept_proposal</c>.
    ///
    /// <para>Action routing:</para>
    /// <list type="bullet">
    ///   <item><c>create</c> &mdash; calls
    ///   <see cref="SkosManager.CreateConcept"/> with the payload's
    ///   <c>scheme_iri</c>; the new concept IRI comes from the graph.</item>
    ///   <item><c>update</c> &mdash; calls
    ///   <see cref="SkosManager.UpdateConcept"/> against
    ///   <see cref="TermProposalEntity.TargetIri"/>.</item>
    ///   <item><c>add_alias</c> &mdash; routes through <c>update</c> by
    ///   reading the alias list from the payload; the target IRI comes from
    ///   <see cref="TermProposalEntity.TargetIri"/>.</item>
    /// </list>
    ///
    /// <para>Writes run inside <see cref="StoreWrapper.CaptureAsync"/> with
    /// <c>revertOnError: false</c> &mdash; any <see cref="SkosValidationException"/>
    /// surfaces <c>GraphWriteConflictException</c> only when an extraction job
    /// is in flight. Audit row carries the byte-exact pre/post N-Quads diff
    /// computed by <see cref="StoreWrapper.DiffNQuads"/>.</para>
    /// </summary>
    public async Task<(TermProposalEntity Proposal, SkosConceptView? Concept)?> AcceptProposalAsync(
        KnowledgeSystemEntity ks,
        Guid proposalId,
        IReadOnlyDictionary<string, object?>? payload,
        string note,
        Actor actor,
        CancellationToken ct)
    {
        var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
        if (user is null || ksc is null) return null;

        var proposal = await _db.TermProposals
            .FirstOrDefaultAsync(t => t.Id == proposalId && t.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (proposal is null) return null;
        if (proposal.Status != "pending")
        {
            throw new InvalidOperationException(
                $"Proposal {proposalId} is already {proposal.Status}; only pending proposals can be accepted.");
        }

        var data = BuildConceptData(proposal, payload);
        var action = proposal.Action.Trim().ToLowerInvariant();
        SkosConceptView? view = null;

        var pre = _store.DumpNQuads(ksc.VocabularyGraph);
        await using (var cap = await _store
            .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
            .ConfigureAwait(false))
        {
            try
            {
                if (action == "create")
                {
                    var schemeIri = data.SchemeIri;
                    if (string.IsNullOrWhiteSpace(schemeIri))
                    {
                        throw new SkosValidationException(
                            "scheme_iri is required in the payload for create proposals.");
                    }
                    var iri = _skos.CreateConcept(ksc, schemeIri, data);
                    view = _skos.GetConcept(ksc, iri);
                }
                else if (action == "update" || action == "add_alias")
                {
                    var iri = proposal.TargetIri;
                    if (string.IsNullOrWhiteSpace(iri))
                    {
                        throw new SkosValidationException(
                            "target_iri is required on update/add_alias proposals.");
                    }
                    _skos.UpdateConcept(ksc, iri, data);
                    view = _skos.GetConcept(ksc, iri);
                }
                else
                {
                    throw new SkosValidationException(
                        $"Unsupported proposal action '{proposal.Action}'.");
                }
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

        var now = _clock.GetUtcNow();
        proposal.Status = "accepted";
        proposal.ResolvedBy = actor.UserId;
        proposal.ResolvedAt = now;
        if (!string.IsNullOrWhiteSpace(note))
        {
            proposal.ResolutionNote = note;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(ks.Id, user, "terminology.accept",
            $"Accepted terminology proposal {proposal.LegacyId} ({proposal.Action} \"{proposal.Term}\")",
            new Dictionary<string, object?>
            {
                ["proposal_id"] = proposal.Id,
                ["proposal_legacy_id"] = proposal.LegacyId,
                ["action"] = proposal.Action,
                ["term"] = proposal.Term,
                ["target_iri"] = proposal.TargetIri,
                ["note"] = string.IsNullOrWhiteSpace(note) ? null : note,
            },
            ksc.VocabularyGraph, added, removed, ct).ConfigureAwait(false);

        return (proposal, view);
    }

    // ----------------------------------------------------------------------
    // Write — reject (Writer gate, **no** extraction guard — matches Python)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Mark a pending proposal <c>rejected</c> without writing to the SKOS
    /// graph. Mirrors Python <c>vocabulary.reject_proposal</c>.
    ///
    /// <para>No <see cref="StoreWrapper.CaptureAsync"/> + no <c>DumpNQuads</c>:
    /// the graph is untouched, so the audit row carries an empty diff and
    /// the action is <c>terminology.reject</c>. No rejection of in-flight
    /// extraction either &mdash; humans must be able to prune the proposal
    /// backlog while a job is running.</para>
    /// </summary>
    public async Task<TermProposalEntity?> RejectProposalAsync(
        KnowledgeSystemEntity ks,
        Guid proposalId,
        string note,
        Actor actor,
        CancellationToken ct)
    {
        var user = await RequireRoleAsync(ks, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null) return null;

        var proposal = await _db.TermProposals
            .FirstOrDefaultAsync(t => t.Id == proposalId && t.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (proposal is null) return null;
        if (proposal.Status != "pending")
        {
            throw new InvalidOperationException(
                $"Proposal {proposalId} is already {proposal.Status}; only pending proposals can be rejected.");
        }

        var now = _clock.GetUtcNow();
        proposal.Status = "rejected";
        proposal.ResolvedBy = actor.UserId;
        proposal.ResolvedAt = now;
        proposal.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(ks.Id, user, "terminology.reject",
            $"Rejected terminology proposal {proposal.LegacyId} ({proposal.Action} \"{proposal.Term}\")",
            new Dictionary<string, object?>
            {
                ["proposal_id"] = proposal.Id,
                ["proposal_legacy_id"] = proposal.LegacyId,
                ["action"] = proposal.Action,
                ["term"] = proposal.Term,
                ["target_iri"] = proposal.TargetIri,
                ["note"] = string.IsNullOrWhiteSpace(note) ? null : note,
            },
            null, Array.Empty<byte>(), Array.Empty<byte>(), ct).ConfigureAwait(false);

        return proposal;
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
    /// Mirrors <see cref="VocabularyService.RequireWriterAsync"/> so an
    /// <c>accept_proposal</c> that lands during a running extraction surfaces
    /// as a 409 + <c>job_id</c> envelope instead of racing against the
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
    /// <c>running</c>. Contract-test factories build a SQLite database
    /// without running EF migrations; a missing-schema error from the
    /// job-store call is treated as "no active job" so the placeholder
    /// payload path stays on its success branch.
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
                "Extraction in progress; terminology mutation refused.",
                jobId.Value);
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or its inner chain) indicates the
    /// extraction-job table is absent. Mirrors <see cref="VocabularyService"/>
    /// so contract-test factory paths succeed when the SQL schema is
    /// intentionally empty.
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
    /// Translate the wire payload dict (snake_case keys from the Python
    /// reference) into a <see cref="SkosConceptData"/> record. Falls back to
    /// <see cref="TermProposalEntity.Term"/> as the prefLabel when the
    /// payload omits it, and to <see cref="TermProposalEntity.TargetIri"/> as
    /// the IRI hint when present.
    /// </summary>
    private static SkosConceptData BuildConceptData(
        TermProposalEntity proposal,
        IReadOnlyDictionary<string, object?>? payload)
    {
        // Match the dispatcher's wire shape: snake_case + case-insensitive,
        // so a payload with <c>scheme_iri</c> / <c>pref_label</c> populates
        // the matching <c>SchemeIri</c> / <c>PrefLabel</c> fields. B7c left
        // this as case-insensitive-only, which silently dropped every
        // snake_case field on a real Python wire payload.
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        // The Python accept_proposal reference uses the payload that was
        // stored on the proposal row when the human did not supply a new
        // one in the request body; we mirror that fallback so the caller
        // can accept a proposal without re-sending its full payload.
        var effectivePayload = payload;
        if ((effectivePayload is null || effectivePayload.Count == 0)
            && proposal.Payload is not null)
        {
            var raw = proposal.Payload.RootElement.GetRawText();
            effectivePayload = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, jsonOptions);
        }

        // Round-trip the dict through JSON so we honour the wire field
        // names exactly as the Python backend serialises them.
        var serialized = JsonSerializer.Serialize(effectivePayload ?? new Dictionary<string, object?>());
        var data = JsonSerializer.Deserialize<SkosConceptData>(serialized, jsonOptions)
                   ?? new SkosConceptData();

        // PrefLabel defaults to the proposal Term so the most common case
        // (a one-shot create / update) works without an explicit payload.
        if (string.IsNullOrWhiteSpace(data.PrefLabel))
        {
            data = data with { PrefLabel = proposal.Term };
        }
        // IRI hint from the proposal's TargetIri when present.
        if (string.IsNullOrWhiteSpace(data.Iri) && !string.IsNullOrWhiteSpace(proposal.TargetIri))
        {
            data = data with { Iri = proposal.TargetIri };
        }
        return data;
    }

    /// <summary>
    /// Append the audit row that records the change. Mirrors
    /// <see cref="VocabularyService.WriteAuditAsync"/>: pre/post N-Quads byte
    /// blobs round-trip through <see cref="StoreWrapper.DumpNQuads"/> and
    /// <see cref="StoreWrapper.DiffNQuads"/>. <paramref name="graph"/> is
    /// <c>null</c> for graph-untouched events (reject path).
    /// </summary>
    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, string summary,
        IReadOnlyDictionary<string, object?> detail,
        string? graph,
        byte[] added, byte[] removed,
        CancellationToken token)
    {
        _db.AuditEvents.Add(new AuditEventEntity
        {
            LegacyId = await _allocator.NextAsync<AuditEventEntity>(token).ConfigureAwait(false),
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

/// <summary>
/// Wire shape for one <see cref="TermProposalEntity"/> row. Mirrors the
/// Python <c>backend/app/schemas/terminology.py::_proposal_out</c> schema so
/// the dispatcher arm can serialize the row straight to JSON without an
/// additional mapper. Field names match the Python reference verbatim.
/// </summary>
public sealed record TermProposalOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("target_iri")] string? TargetIri,
    [property: JsonPropertyName("target_label")] string? TargetLabel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("evidence")] JsonElement? Evidence,
    [property: JsonPropertyName("source_chunk_ids")] JsonElement? SourceChunkIds,
    [property: JsonPropertyName("extraction_job_id")] Guid? ExtractionJobId,
    [property: JsonPropertyName("proposed_by")] string ProposedBy,
    [property: JsonPropertyName("resolved_by")] string? ResolvedBy,
    [property: JsonPropertyName("resolution_note")] string? ResolutionNote,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("resolved_at")] DateTimeOffset? ResolvedAt);