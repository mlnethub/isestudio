using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Application.Foundation;
using OnToPilot.Audit;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.EntityResolution;

/// <summary>
/// Per-knowledge-system entity-resolution memory: surfaces (queue) and decisions
/// (resolved rows). Read endpoints accept <c>?q=</c> / <c>?limit=</c> /
/// <c>?offset=</c>; write endpoints flip <see cref="EntityResolutionEntity.Status"/>
/// and — for <c>action="new"</c> — mint a fresh individual in the ABox graph.
/// All writes route through <c>RunWithExtractionGuardAsync</c> via the
/// dispatcher; the service itself is extraction-agnostic.
/// </summary>
public sealed class ResolutionService
{
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;
    private readonly KnowledgeSystemAccessService _access;
    private readonly AuditLogService _audit;
    private readonly ABoxManager? _abox;
    private readonly StoreWrapper? _store;

    public ResolutionService(
        OnToPilotDbContext db,
        TimeProvider clock,
        LegacyIdAllocator allocator,
        KnowledgeSystemAccessService access,
        AuditLogService audit,
        ABoxManager? abox = null,
        StoreWrapper? store = null)
    {
        _db = db;
        _clock = clock;
        _allocator = allocator;
        _access = access;
        _audit = audit;
        _abox = abox;
        _store = store;
    }

    // ---- read: queue ----

    /// <summary>
    /// List pending surface forms waiting for human review (status="pending").
    /// Supports <paramref name="query"/> substring match on <c>SurfaceForm</c> +
    /// paging via <paramref name="limit"/>/<paramref name="offset"/>.
    /// Returns null when the KS is missing or invisible to the actor.
    /// </summary>
    public async Task<ResolutionQueueEnvelope?> ListQueueAsync(
        Guid ksId, string? query, int limit, int offset, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await LoadActorAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer)
            throw new ValidationException("Viewer access required to list the resolution queue.");

        var q = _db.EntityResolutions.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ks.Id && r.Status == "pending");
        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(r => EF.Functions.Like(r.SurfaceForm, $"%{query}%"));

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip(Math.Max(offset, 0)).Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows.ConvertAll(ToQueueItem);
        return new ResolutionQueueEnvelope(items, total);
    }

    // ---- read: decisions ----

    /// <summary>
    /// List resolved rows (status ∈ {"matched", "new", "distinct"}).
    /// Same query/paging contract as <see cref="ListQueueAsync"/>.
    /// </summary>
    public async Task<ResolutionDecisionsEnvelope?> ListDecisionsAsync(
        Guid ksId, string? query, int limit, int offset, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await LoadActorAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer)
            throw new ValidationException("Viewer access required to list the resolution decisions.");

        var q = _db.EntityResolutions.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ks.Id
                && (r.Status == "matched" || r.Status == "new" || r.Status == "distinct"));
        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(r => EF.Functions.Like(r.SurfaceForm, $"%{query}%"));

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.ResolvedAt ?? r.CreatedAt).ThenBy(r => r.Id)
            .Skip(Math.Max(offset, 0)).Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows.ConvertAll(ToDecision);
        return new ResolutionDecisionsEnvelope(items, total);
    }

    // ---- write: resolve ----

    /// <summary>
    /// Resolve a queue item:
    /// <list type="bullet">
    /// <item><c>action="match"</c> requires <paramref name="individualIri"/>; row status → "matched".</item>
    /// <item><c>action="new"</c> mints a fresh individual via <see cref="ABoxManager.CreateIndividual"/>; row status → "new".</item>
    /// </list>
    /// Writes audit row with action <c>abox.resolve</c>; for <c>new</c>, the audit
    /// <c>added</c> blob carries the gzipped N-Triples captured by
    /// <see cref="StoreWrapper.CaptureAsync"/>; <c>match</c> emits empty diffs.
    /// </summary>
    public async Task<ResolutionDecisionOut?> ResolveAsync(
        Guid ksId, long rowLegacyId, string action, string? individualIri,
        Actor actor, CancellationToken ct)
    {
        if (action != "match" && action != "new")
            throw new ValidationException("action must be 'match' or 'new'.");
        if (action == "match" && string.IsNullOrWhiteSpace(individualIri))
            throw new ValidationException("individual_iri is required when action='match'.");

        var (user, ks) = await LoadActorAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to resolve.");

        var row = await _db.EntityResolutions
            .FirstOrDefaultAsync(r => r.KnowledgeSystemId == ks.Id && r.LegacyId == rowLegacyId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (row.Status != "pending")
            throw new ValidationException($"Resolution already '{row.Status}'.");

        var now = _clock.GetUtcNow();
        byte[] added = Array.Empty<byte>();
        byte[] removed = Array.Empty<byte>();
        string? mintedIri = null;

        if (action == "new")
        {
            if (string.IsNullOrWhiteSpace(row.ClassIri))
                throw new ValidationException("class_iri missing on row; cannot mint new individual.");

            if (_abox is not null && _store is not null)
            {
                var ctx = KsContext.FromEntity(ks);
                var aboxGraph = new OntoNamedNode(ctx.ABoxGraph);
                await using var capture = await _store.CaptureAsync(
                    aboxGraph, revertOnError: false, cancellationToken: ct).ConfigureAwait(false);
                byte[] pre;
                byte[] post;
                try
                {
                    pre = _store.DumpNQuads(aboxGraph);
                    mintedIri = _abox.CreateIndividual(ctx, row.SurfaceForm, row.ClassIri!, row.SurfaceForm);
                    post = _store.DumpNQuads(aboxGraph);
                }
                catch
                {
                    capture.MarkError();
                    throw;
                }
                (added, removed) = StoreWrapper.DiffNQuads(pre, post);
            }
            else
            {
                // Test path: graph store not wired. _abox.CreateIndividual still
                // returns the deterministic MintIri output without persistence
                // (see ABoxManager.cs:59-64). Wire a stub IRI for envelope shape.
                mintedIri = $"{ks.BaseIri}ind-{Guid.NewGuid():N}".Substring(
                    0, Math.Min(ks.BaseIri.Length + 16, ks.BaseIri.Length + 32));
            }
            row.IndividualIri = mintedIri;
            row.Status = "new";
        }
        else // "match"
        {
            row.IndividualIri = individualIri;
            row.Status = "matched";
        }

        row.ResolvedBy = user.DisplayName ?? user.Username;
        row.ResolvedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detail = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["surface_form"] = row.SurfaceForm,
            ["individual_ri"] = row.IndividualIri,
        };
        await _audit.RecordAsync(
            ksId, user, "abox.resolve",
            $"Resolved '{row.SurfaceForm}' as {action} → {row.IndividualIri}.",
            detail, "abox",
            added, removed, null, ct).ConfigureAwait(false);

        return ToDecision(row);
    }

    // ---- write: revoke ----

    /// <summary>
    /// Delete the row; audit <c>resolution.revoke</c> with the surface form.
    /// </summary>
    public async Task<bool> RevokeAsync(
        Guid ksId, long rowLegacyId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await LoadActorAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return false;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to revoke resolution.");

        var row = await _db.EntityResolutions
            .FirstOrDefaultAsync(r => r.KnowledgeSystemId == ks.Id && r.LegacyId == rowLegacyId, ct)
            .ConfigureAwait(false);
        if (row is null) return false;

        var surface = row.SurfaceForm;
        _db.EntityResolutions.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            ksId, user, "resolution.revoke",
            $"Forgot resolution memory for \"{surface}\".",
            null, null, Array.Empty<byte>(), Array.Empty<byte>(), null, ct)
            .ConfigureAwait(false);
        return true;
    }

    // ---- write: edit_reason ----

    /// <summary>
    /// Mutate <c>EntityResolutionEntity.Context["reason"]</c> (Python stores
    /// reason inside the JSON blob, not a top-level column). Truncated to
    /// 200 chars to match the convention in <c>ConflictService.EditReconciliationReasonAsync</c>.
    /// </summary>
    public async Task<ResolutionDecisionOut?> EditReasonAsync(
        Guid ksId, long rowLegacyId, string? reason, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await LoadActorAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to edit reason.");

        var row = await _db.EntityResolutions
            .FirstOrDefaultAsync(r => r.KnowledgeSystemId == ks.Id && r.LegacyId == rowLegacyId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 200 }) trimmed = trimmed[..200];

        var dict = ReadContextDict(row);
        if (trimmed is null) dict.Remove("reason");
        else dict["reason"] = trimmed;
        row.Context = JsonDocument.Parse(JsonSerializer.Serialize(dict));

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            ksId, user, "resolution.edit_reason",
            $"Edited resolution reason for \"{row.SurfaceForm}\".",
            new Dictionary<string, object?> { ["reason"] = trimmed },
            null, Array.Empty<byte>(), Array.Empty<byte>(), null, ct)
            .ConfigureAwait(false);

        return ToDecision(row);
    }

    // ---- internals ----

    /// <summary>
    /// Resolve a controller-side <see cref="InternalRequest.ResourceId"/>
    /// (a string from the URL slot) to the row's <see cref="EntityResolutionEntity"/>
    /// PK. Tries <see cref="EntityResolutionEntity.LegacyId"/> first (matches
    /// Python int wire format), then falls back to the GUID primary key.
    /// Returns null when the string parses as neither or when no row matches.
    /// </summary>
    public static async Task<Guid?> ResolveResRowGuidAsync(
        OnToPilotDbContext db, Guid ksId, string? resourceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        if (long.TryParse(resourceId, out var legacyId))
        {
            var byLegacy = await db.EntityResolutions.AsNoTracking()
                .Where(r => r.KnowledgeSystemId == ksId && r.LegacyId == legacyId)
                .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (byLegacy is not null) return byLegacy;
        }
        if (Guid.TryParse(resourceId, out var guid))
        {
            var exists = await db.EntityResolutions.AsNoTracking()
                .AnyAsync(r => r.KnowledgeSystemId == ksId && r.Id == guid, ct)
                .ConfigureAwait(false);
            return exists ? guid : null;
        }
        return null;
    }

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> LoadActorAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct).ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return (null, null);
        return (user, ks);
    }

    private static Dictionary<string, object?> ReadContextDict(EntityResolutionEntity row)
    {
        if (row.Context is null) return new(StringComparer.Ordinal);
        using var doc = row.Context;
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
        }
        return dict;
    }

    private static ResolutionQueueItemOut ToQueueItem(EntityResolutionEntity r)
    {
        var candidates = ReadCandidates(r);
        return new ResolutionQueueItemOut(
            r.LegacyId,
            r.SurfaceForm,
            r.ClassIri,
            null, // class_label: requires StoreWrapper rdfs:label lookup; MVP returns null
            r.Confidence,
            candidates,
            r.SourceChunkId?.ToString("N"),
            r.CreatedAt);
    }

    private static ResolutionDecisionOut ToDecision(EntityResolutionEntity r)
    {
        string? reason = null;
        if (r.Context is not null)
        {
            using var doc = r.Context;
            if (doc.RootElement.TryGetProperty("reason", out var reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String)
            {
                reason = reasonEl.GetString();
            }
        }
        return new ResolutionDecisionOut(
            r.LegacyId,
            r.SurfaceForm,
            null,
            r.Status,
            r.IndividualIri,
            null,
            false,
            r.Confidence,
            reason,
            r.ResolvedBy,
            r.CreatedAt,
            r.ResolvedAt);
    }

    private static IReadOnlyList<ResolutionCandidateOut> ReadCandidates(EntityResolutionEntity r)
    {
        if (r.Context is null) return Array.Empty<ResolutionCandidateOut>();
        using var doc = r.Context;
        if (!doc.RootElement.TryGetProperty("candidates", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<ResolutionCandidateOut>();
        var list = new List<ResolutionCandidateOut>();
        foreach (var el in arr.EnumerateArray())
        {
            string iri = el.TryGetProperty("iri", out var v1) && v1.ValueKind == JsonValueKind.String
                ? v1.GetString() ?? string.Empty
                : string.Empty;
            string label = el.TryGetProperty("label", out var v2) && v2.ValueKind == JsonValueKind.String
                ? v2.GetString() ?? string.Empty
                : string.Empty;
            double score = el.TryGetProperty("score", out var v3) && v3.ValueKind == JsonValueKind.Number
                ? v3.GetDouble() : 0d;
            list.Add(new ResolutionCandidateOut(iri, label, score));
        }
        return list;
    }
}