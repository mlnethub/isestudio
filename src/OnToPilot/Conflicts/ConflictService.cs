using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;

namespace OnToPilot.Conflicts;

/// <summary>
/// Conflict queue + reconciliation memory CRUD. Aligned with the Python
/// <c>backend/app/api/conflicts.py</c> surface (and its companion
/// <c>backend/app/ontology/conflicts.py</c> detector).
///
/// <para>The service is the first dispatcher-routed service that touches
/// the in-memory graph layer via <see cref="StoreWrapper"/>. Production
/// code resolves a single singleton <see cref="StoreWrapper"/> opened at
/// the configured workspace path; test / contract-only factories pass
/// <c>null</c> and the structural detection paths degrade to "return
/// what's already in the table" so the SQL contract stays testable without
/// an embedded Oxigraph.</para>
/// </summary>
public sealed class ConflictService
{
    /// <summary>Injected optionally so tests can skip graph detection.</summary>
    private readonly StoreWrapper? _store;

    /// <summary>Used for the <c>extraction_active</c> guard on resolve / dismiss.</summary>
    private readonly ExtractionJobStore? _jobs;

    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;

    public ConflictService(
        OnToPilotDbContext db,
        TimeProvider clock,
        LegacyIdAllocator allocator,
        ExtractionJobStore? jobs = null,
        StoreWrapper? store = null)
    {
        _db = db;
        _clock = clock;
        _allocator = allocator;
        _jobs = jobs;
        _store = store;
    }

    // ----------------------------------------------------------------------
    // List
    // ----------------------------------------------------------------------

    /// <summary>
    /// List every conflict for <paramref name="ksId"/>, filtered by
    /// <paramref name="status"/> (default <c>open</c>; pass <c>all</c>
    /// to bypass) and optionally narrowed by <paramref name="ctype"/>.
    /// Ordered most-severe first (matching the Python <c>order_by(severity.desc(), id)</c>).
    /// </summary>
    public async Task<IReadOnlyList<ConflictOut>> ListAsync(
        long ksId,
        string status,
        string? ctype,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null)
        {
            return Array.Empty<ConflictOut>();
        }
        var query = _db.Conflicts.AsNoTracking()
            .Where(c => c.KnowledgeSystemId == ks.Id);
        if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => c.Status == status);
        }
        if (!string.IsNullOrEmpty(ctype))
        {
            query = query.Where(c => c.Ctype == ctype);
        }
        var rows = await query
            .OrderByDescending(c => c.Severity == "error" ? 1 : 0)
            .ThenBy(c => c.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var legacyId = ks.LegacyId;
        return rows.ConvertAll(c => ToOut(c, legacyId));
    }

    // ----------------------------------------------------------------------
    // Detect
    // ----------------------------------------------------------------------

    /// <summary>
    /// Re-detect conflicts for the KS, reconcile with the stored rows, and
    /// return the freshly-synced open list. Mirrors Python
    /// <c>sync_conflicts(session, ks, semantic=True)</c>.
    /// </summary>
    public async Task<IReadOnlyList<ConflictOut>> DetectAsync(
        long ksId,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Knowledge system {ksId} not found.");

        if (_store is null)
        {
            // No graph store wired — preserve the existing DB rows and
            // return them as-is. This is the contract-test path; the
            // SQLite-backed factory doesn't ship an Oxigraph.
            return await ListAsync(ksId, "open", ctype: null, ct).ConfigureAwait(false);
        }

        var detected = ConflictDetection.Detect(_store, ks.GraphIri, semantic: true);
        var bySig = detected.ToDictionary(d => d.Signature, StringComparer.Ordinal);
        var existing = await _db.Conflicts
            .Where(c => c.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingBySig = existing.ToDictionary(c => c.Signature, StringComparer.Ordinal);
        var newOnes = new List<ConflictEntity>();

        foreach (var (sig, d) in bySig)
        {
            var payload = ToPayloadJson(d.Entities, d.Resolutions);
            if (!existingBySig.TryGetValue(sig, out var row))
            {
                // AllocateManyAndPersistAsync adds to the change-tracker
                // internally; we collect un-keyed entities here so the
                // allocator can assign distinct LegacyIds under the
                // per-table pg_advisory_xact_lock. Without this hop, two
                // detected conflicts in a single batch would both write
                // LegacyId=0 and the second SaveChanges would trip
                // ux_conflict_legacy_id with SqlState=23505.
                newOnes.Add(new ConflictEntity
                {
                    Id = Guid.NewGuid(),
                    KnowledgeSystemId = ks.Id,
                    Signature = sig,
                    Ctype = d.Ctype,
                    Severity = d.Severity,
                    Status = "open",
                    Title = d.Title,
                    Detail = d.Detail,
                    Payload = payload,
                    CreatedAt = _clock.GetUtcNow(),
                });
            }
            else if (row.Status == "dismissed")
            {
                // user judged it a non-issue — leave it alone
                continue;
            }
            else
            {
                row.Status = "open";
                row.ResolvedAt = null;
                row.Resolution = null;
                row.Title = d.Title;
                row.Detail = d.Detail;
                row.Severity = d.Severity;
                row.Payload = payload;
            }
        }

        foreach (var row in existing)
        {
            if (row.Status == "open" && !bySig.ContainsKey(row.Signature))
            {
                row.Status = "resolved";
                row.ResolvedAt = _clock.GetUtcNow();
                row.Resolution = "auto-cleared";
            }
        }

        if (newOnes.Count > 0)
        {
            await _allocator.AllocateManyAndPersistAsync(newOnes, ct).ConfigureAwait(false);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await ListAsync(ksId, "open", ctype: null, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // Get context
    // ----------------------------------------------------------------------

    /// <summary>
    /// Return the conflict plus the ranked axiom-evidence bundles an
    /// operator needs to make a decision. Each evidence bundle pulls its
    /// provenance from <see cref="AxiomProvenanceEntity"/> via the canonical
    /// <c>axiom_key</c> mapping (<c>_conflict_axiom_keys</c> in Python).
    /// </summary>
    public async Task<ConflictContext?> GetContextAsync(
        long ksId,
        Guid conflictId,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var row = await _db.Conflicts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conflictId && c.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        var entities = ReadEntities(row);
        var labelsByLocal = entities
            .Where(e => !string.IsNullOrEmpty(e.Iri))
            .ToDictionary(e => IriLocal(e.Iri), e => string.IsNullOrEmpty(e.Label) ? IriLocal(e.Iri) : e.Label, StringComparer.Ordinal);
        var exactKeys = ConflictAxiomKeys(row);
        var structuralContext = row.Ctype is "disjoint_subclass" or "disjoint_common";
        var entityLocals = labelsByLocal.Keys.ToList();

        var provFilters = new List<string>();
        provFilters.AddRange(exactKeys);
        if (structuralContext)
        {
            provFilters.AddRange(entityLocals.Select(local => $"|{local}"));
        }
        var provRows = provFilters.Count > 0
            ? await _db.AxiomProvenances.AsNoTracking()
                .Where(p => p.KnowledgeSystemId == ks.Id
                    && provFilters.Any(f => p.AxiomKey.Contains(f)))
                .ToListAsync(ct)
                .ConfigureAwait(false)
            : new List<AxiomProvenanceEntity>();

        // Rank keys: exact matches first, then structural context hits.
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var entityLocalSet = new HashSet<string>(entityLocals, StringComparer.Ordinal);
        foreach (var prov in provRows)
        {
            var parts = prov.AxiomKey.Split('|', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToHashSet(StringComparer.Ordinal);
            if (exactKeys.Contains(prov.AxiomKey))
            {
                ranks[prov.AxiomKey] = 0;
            }
            else if (structuralContext && parts.Overlaps(entityLocalSet) && prov.AxiomKey.StartsWith("subClassOf|", StringComparison.Ordinal))
            {
                ranks.TryAdd(prov.AxiomKey, 1);
            }
        }
        var rankedKeys = ranks.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
        var rankedSet = new HashSet<string>(rankedKeys, StringComparer.Ordinal);

        var relevant = provRows.Where(p => rankedSet.Contains(p.AxiomKey)).ToList();
        var chunkIds = relevant.Where(p => p.ChunkId.HasValue).Select(p => p.ChunkId!.Value).Distinct().ToList();
        var chunks = chunkIds.Count > 0
            ? await _db.Chunks.AsNoTracking().Where(c => chunkIds.Contains(c.Id)).ToListAsync(ct).ConfigureAwait(false)
            : new List<ChunkEntity>();
        var chunkMap = chunks.ToDictionary(c => c.Id, c => c);
        var docIds = chunks.Select(c => c.DocumentId).Distinct().ToList();
        var docs = docIds.Count > 0
            ? await _db.Documents.AsNoTracking()
                .Where(d => docIds.Contains(d.Id) && d.KnowledgeSystemId == ks.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false)
            : new List<DocumentEntity>();
        var docMap = docs.ToDictionary(d => d.Id, d => d);

        var byKey = relevant
            .GroupBy(p => p.AxiomKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var evidence = new List<ConflictEvidence>();
        foreach (var key in rankedKeys)
        {
            if (!byKey.TryGetValue(key, out var keyRows)) continue;
            var seen = new HashSet<Guid>();
            var sources = new List<ConflictEvidenceSource>();
            foreach (var prov in keyRows)
            {
                if (!prov.ChunkId.HasValue) continue;
                if (!seen.Add(prov.ChunkId.Value)) continue;
                if (!chunkMap.TryGetValue(prov.ChunkId.Value, out var chunk)) continue;
                docMap.TryGetValue(chunk.DocumentId, out var doc);
                sources.Add(new ConflictEvidenceSource(
                    ChunkId: chunk.Id,
                    ChunkIndex: chunk.Idx,
                    DocumentId: doc?.Id,
                    Document: doc?.OriginalFilename,
                    Folder: doc?.Folder,
                    JobId: prov.JobId,
                    Snippet: (chunk.Text ?? string.Empty).Trim()));
            }
            if (sources.Count == 0) continue;
            evidence.Add(new ConflictEvidence(
                AxiomKey: key,
                Description: DescribeAxiom(key, local => labelsByLocal.TryGetValue(local, out var l) ? l : local),
                SourceCount: sources.Count,
                Sources: sources));
        }

        return new ConflictContext(ToOut(row, ks.LegacyId), evidence);
    }

    // ----------------------------------------------------------------------
    // Dismiss / reopen
    // ----------------------------------------------------------------------

    public async Task<ConflictOut?> DismissAsync(
        long ksId,
        Guid conflictId,
        string actor,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var row = await _db.Conflicts.FirstOrDefaultAsync(c => c.Id == conflictId && c.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        // No extraction-guard on dismiss in Python — kept permissive so
        // operators can clear noise during a long extraction.
        _ = actor;
        row.Status = "dismissed";
        row.ResolvedAt = _clock.GetUtcNow();
        row.Resolution = "dismissed";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToOut(row, ks.LegacyId);
    }

    public async Task<ConflictOut?> ReopenAsync(
        long ksId,
        Guid conflictId,
        string actor,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var row = await _db.Conflicts.FirstOrDefaultAsync(c => c.Id == conflictId && c.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (row.Status == "open")
        {
            throw new InvalidOperationException("Conflict is already open.");
        }
        _ = actor;
        row.Status = "open";
        row.ResolvedAt = null;
        row.Resolution = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToOut(row, ks.LegacyId);
    }

    // ----------------------------------------------------------------------
    // Resolve
    // ----------------------------------------------------------------------

    /// <summary>
    /// Apply the chosen resolution's editor op against the TBox graph
    /// (within a capture so a thrown exception reverts the writes), flip
    /// the conflict row to <c>resolved</c>, and return the freshly-synced
    /// open list + rebuilt ontology view so the frontend can re-render
    /// without a second round-trip.
    /// </summary>
    public async Task<ResolveConflictResponse?> ResolveAsync(
        long ksId,
        Guid conflictId,
        string resolutionId,
        string actor,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;

        if (_jobs is not null)
        {
            var active = await _jobs.FindActiveJobAsync(ks.Id, ct).ConfigureAwait(false);
            if (active is not null)
            {
                throw new InvalidOperationException(
                    "An extraction is in progress; try again after it finishes.");
            }
        }

        var row = await _db.Conflicts.FirstOrDefaultAsync(c => c.Id == conflictId && c.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (row.Status != "open")
        {
            throw new InvalidOperationException($"Conflict already {row.Status}.");
        }

        var resolutions = ReadResolutions(row);
        var chosen = resolutions.FirstOrDefault(r => string.Equals(r.Id, resolutionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Unknown resolution id.");

        // The Python backend also cascades merges into the ABox graph
        // (merge_classes / merge_properties). Those ops aren't implemented
        // in OntologyEditor yet; the detector surfaces them as no-op
        // resolutions so the UI can render the option greyed out. A
        // delete_axiom resolution runs end-to-end here.
        if (_store is not null && !IsNoOpResolution(chosen.Op))
        {
            var editor = new OntologyEditor(_store);
            try
            {
                await editor.ApplyEditAsync(ks.GraphIri, ks.BaseIri, chosen.Op, ct).ConfigureAwait(false);
            }
            catch (OntologyEditException ex)
            {
                throw new InvalidOperationException($"Resolution failed: {ex.Message}", ex);
            }
        }

        row.Status = "resolved";
        row.ResolvedAt = _clock.GetUtcNow();
        row.Resolution = resolutionId;

        // Record the agent / human decision into the learned-memory table
        // for domain/range conflicts so future auto-reconciliation consults it.
        if (row.Ctype is "domain_multi" or "range_multi")
        {
            await RecordDomainRangeReconciliation(ks.Id, row, resolutionId, chosen.Op, actor, ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-sync open conflicts (semantic=False path mirrors Python
        // resolve_conflict — no LLM/embedding pass after a manual fix).
        var openConflicts = _store is null
            ? await ListAsync(ksId, "open", ctype: null, ct).ConfigureAwait(false)
            : await DetectAndSyncWithoutSemanticAsync(ks, ct).ConfigureAwait(false);

        // Build a minimal view stub so the wire shape matches Python; the
        // full build_view needs ShapeBuilder + ABoxManager wiring that
        // lands in Block 6. The frontend treats null `view` as
        // "no incremental rebuild needed".
        JsonElement view = default;
        return new ResolveConflictResponse(row.LegacyId, openConflicts, view);
    }

    // ----------------------------------------------------------------------
    // Reconciliations
    // ----------------------------------------------------------------------

    public async Task<ReconciliationListResponse> ListReconciliationsAsync(
        long ksId,
        string? query,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return new ReconciliationListResponse(Array.Empty<ReconciliationOut>(), 0);

        var q = _db.TboxReconciliations.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ks.Id);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim()}%";
            q = q.Where(r => EF.Functions.Like(r.PropertyLabel, like));
        }
        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.Id)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var legacyId = ks.LegacyId;
        return new ReconciliationListResponse(rows.ConvertAll(r => ToReconciliationOut(r, legacyId)), total);
    }

    public async Task<long?> RevokeReconciliationAsync(
        long ksId,
        Guid reconciliationId,
        string actor,
        CancellationToken ct)
    {
        _ = actor;
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var row = await _db.TboxReconciliations.FirstOrDefaultAsync(r => r.Id == reconciliationId && r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        var legacy = row.LegacyId;
        _db.TboxReconciliations.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return legacy;
    }

    public async Task<(Guid Id, string Reason)?> EditReconciliationReasonAsync(
        long ksId,
        Guid reconciliationId,
        string? reason,
        string actor,
        CancellationToken ct)
    {
        _ = actor;
        var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var row = await _db.TboxReconciliations.FirstOrDefaultAsync(r => r.Id == reconciliationId && r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (row is null) return null;
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200];
        row.Reason = trimmed;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (row.Id, row.Reason ?? string.Empty);
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private async Task<KnowledgeSystemEntity?> ResolveKnowledgeSystemAsync(long ksId, CancellationToken ct)
    {
        return await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.LegacyId == ksId, ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ConflictOut>> DetectAndSyncWithoutSemanticAsync(
        KnowledgeSystemEntity ks,
        CancellationToken ct)
    {
        if (_store is null) return Array.Empty<ConflictOut>();
        var detected = ConflictDetection.Detect(_store, ks.GraphIri, semantic: false);
        var bySig = detected.ToDictionary(d => d.Signature, StringComparer.Ordinal);
        var existing = await _db.Conflicts
            .Where(c => c.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingBySig = existing.ToDictionary(c => c.Signature, StringComparer.Ordinal);
        var newOnes = new List<ConflictEntity>();
        foreach (var (sig, d) in bySig)
        {
            var payload = ToPayloadJson(d.Entities, d.Resolutions);
            if (!existingBySig.TryGetValue(sig, out var row))
            {
                // See DetectAsync: collect un-keyed entities into a list
                // so AllocateManyAndPersistAsync can assign distinct
                // LegacyIds under the per-table pg_advisory_xact_lock.
                newOnes.Add(new ConflictEntity
                {
                    Id = Guid.NewGuid(),
                    KnowledgeSystemId = ks.Id,
                    Signature = sig,
                    Ctype = d.Ctype,
                    Severity = d.Severity,
                    Status = "open",
                    Title = d.Title,
                    Detail = d.Detail,
                    Payload = payload,
                    CreatedAt = _clock.GetUtcNow(),
                });
            }
            else if (row.Status == "dismissed")
            {
                continue;
            }
            else
            {
                row.Status = "open";
                row.ResolvedAt = null;
                row.Resolution = null;
                row.Title = d.Title;
                row.Detail = d.Detail;
                row.Severity = d.Severity;
                row.Payload = payload;
            }
        }
        foreach (var row in existing)
        {
            if (row.Status == "open" && !bySig.ContainsKey(row.Signature))
            {
                row.Status = "resolved";
                row.ResolvedAt = _clock.GetUtcNow();
                row.Resolution = "auto-cleared";
            }
        }
        if (newOnes.Count > 0)
        {
            await _allocator.AllocateManyAndPersistAsync(newOnes, ct).ConfigureAwait(false);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await ListAsync(ks.LegacyId, "open", ctype: null, ct).ConfigureAwait(false);
    }

    private async Task RecordDomainRangeReconciliation(
        Guid ksId,
        ConflictEntity row,
        string resolutionId,
        IReadOnlyDictionary<string, object?> op,
        string actor,
        CancellationToken ct)
    {
        var slot = row.Ctype == "domain_multi" ? "domain" : "range";
        if (!TryReadPayloadEntity(row, 0, out var propIri, out var propLabel)) return;
        var safePropLabel = propLabel ?? string.Empty;
        var safePropIri = propIri ?? string.Empty;
        var candidateIris = ReadEntities(row).Skip(1).Select(e => e.Label).ToList();

        string choice;
        string? chosenLabel = null;
        if (string.Equals(resolutionId, "union", StringComparison.Ordinal))
        {
            choice = "union";
        }
        else if (resolutionId.StartsWith("super-", StringComparison.Ordinal))
        {
            choice = "common_super";
            if (op.TryGetValue(slot, out var clsObj) && clsObj is string clsIri)
            {
                chosenLabel = IriLocal(clsIri);
            }
        }
        else
        {
            choice = "keep";
            if (op.TryGetValue(slot, out var clsObj) && clsObj is string clsIri)
            {
                chosenLabel = IriLocal(clsIri);
            }
        }

        // AllocateAndPersistAsync holds the per-table pg_advisory_xact_lock
        // until COMMIT so a concurrent reconciliation recorded on the
        // same knowledge system cannot both INSERT legacy_id=0 and
        // collide on ux_tboxreconciliation_legacy_id.
        await _allocator.AllocateAndPersistAsync(new TboxReconciliationEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            Slot = slot,
            PropertyLabel = safePropLabel,
            PropertyIri = safePropIri,
            Candidates = ToJsonArray(candidateIris),
            Choice = choice,
            ChosenLabel = chosenLabel,
            ResolvedBy = string.IsNullOrEmpty(actor) ? null : actor,
            CreatedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);
    }

    private static ConflictOut ToOut(ConflictEntity c, long ksLegacyId) =>
        new(
            Id: c.Id,
            KnowledgeSystemId: ksLegacyId,
            Signature: c.Signature,
            Ctype: c.Ctype,
            Severity: c.Severity,
            Status: c.Status,
            Title: c.Title,
            Detail: c.Detail,
            Payload: c.Payload is null ? null : JsonDocument.Parse(c.Payload.RootElement.GetRawText()).RootElement.Clone(),
            CreatedAt: c.CreatedAt,
            ResolvedAt: c.ResolvedAt,
            Resolution: c.Resolution);

    private static ReconciliationOut ToReconciliationOut(TboxReconciliationEntity r, long ksLegacyId) =>
        new(
            Id: r.Id,
            KnowledgeSystemId: ksLegacyId,
            Slot: r.Slot,
            PropertyLabel: r.PropertyLabel,
            PropertyIri: r.PropertyIri,
            Candidates: r.Candidates is null ? null : JsonDocument.Parse(r.Candidates.RootElement.GetRawText()).RootElement.Clone(),
            Choice: r.Choice,
            ChosenLabel: r.ChosenLabel,
            Reason: r.Reason,
            ResolvedBy: r.ResolvedBy,
            CreatedAt: r.CreatedAt);

    private static JsonDocument ToPayloadJson(
        IReadOnlyList<ConflictDetection.EntityRef> entities,
        IReadOnlyList<ConflictDetection.Resolution> resolutions)
    {
        var ents = entities.Select(e => new Dictionary<string, object?> { ["iri"] = e.Iri, ["label"] = e.Label }).ToList();
        var res = resolutions.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["label"] = r.Label,
            ["op"] = r.Op,
        }).ToList();
        var doc = new Dictionary<string, object?>
        {
            ["entities"] = ents,
            ["resolutions"] = res,
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(doc));
    }

    private static JsonDocument ToJsonArray(IReadOnlyList<string> items) =>
        JsonDocument.Parse(JsonSerializer.Serialize(items));

    private static IReadOnlyList<ConflictDetection.EntityRef> ReadEntities(ConflictEntity row)
    {
        if (row.Payload is null) return Array.Empty<ConflictDetection.EntityRef>();
        var root = row.Payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entities", out var entsEl) || entsEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ConflictDetection.EntityRef>();
        }
        var list = new List<ConflictDetection.EntityRef>();
        foreach (var e in entsEl.EnumerateArray())
        {
            var iri = e.TryGetProperty("iri", out var i) ? i.GetString() ?? string.Empty : string.Empty;
            var label = e.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            list.Add(new ConflictDetection.EntityRef(iri, label));
        }
        return list;
    }

    private static bool TryReadPayloadEntity(ConflictEntity row, int index, out string? iri, out string? label)
    {
        iri = null;
        label = null;
        var entities = ReadEntities(row);
        if (index >= entities.Count) return false;
        iri = entities[index].Iri;
        label = entities[index].Label;
        return true;
    }

    private static IReadOnlyList<ConflictDetection.Resolution> ReadResolutions(ConflictEntity row)
    {
        if (row.Payload is null) return Array.Empty<ConflictDetection.Resolution>();
        var root = row.Payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resolutions", out var resEl) || resEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ConflictDetection.Resolution>();
        }
        var list = new List<ConflictDetection.Resolution>();
        foreach (var r in resEl.EnumerateArray())
        {
            var id = r.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
            var label = r.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            var opDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (r.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in opEl.EnumerateObject())
                {
                    opDict[kv.Name] = JsonElementToObject(kv.Value);
                }
            }
            list.Add(new ConflictDetection.Resolution(id, label, opDict));
        }
        return list;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (object)el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    /// <summary>
    /// Mirror of <c>_conflict_axiom_keys</c> in Python — the canonical
    /// provenance keys directly involved in a detected conflict. The
    /// C# port reads from the stored payload so the answer matches what
    /// the detector generated, not a re-derivation.
    /// </summary>
    private static HashSet<string> ConflictAxiomKeys(ConflictEntity row)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var entities = ReadEntities(row);
        var iris = entities.Where(e => !string.IsNullOrEmpty(e.Iri)).Select(e => e.Iri).ToList();

        if (row.Ctype == "duplicate")
        {
            foreach (var iri in iris) keys.Add($"class|{IriLocal(iri)}");
        }
        else if (row.Ctype == "predicate_specialization")
        {
            foreach (var iri in iris)
            {
                var local = IriLocal(iri);
                keys.Add($"objprop|{local}");
                keys.Add($"dataprop|{local}");
            }
        }
        else if ((row.Ctype == "domain_multi" || row.Ctype == "range_multi") && iris.Count > 0)
        {
            var slot = row.Ctype == "domain_multi" ? "domain" : "range";
            var propLocal = IriLocal(iris[0]);
            foreach (var value in iris.Skip(1))
            {
                keys.Add($"{slot}|{propLocal}|{IriLocal(value)}");
            }
        }
        else if ((row.Ctype == "disjoint_subclass" || row.Ctype == "disjoint_common") && iris.Count >= 2)
        {
            var pair = iris.TakeLast(2).Select(IriLocal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            keys.Add($"disjointWith|{pair[0]}|{pair[1]}");
        }
        else if (row.Ctype == "equiv_disjoint" && iris.Count >= 2)
        {
            var sorted = iris.Select(IriLocal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            keys.Add($"disjointWith|{sorted[0]}|{sorted[1]}");
            keys.Add($"equivalentClass|{sorted[0]}|{sorted[1]}");
        }

        // Resolutions that delete an axiom add that axiom's provenance key
        // so the evidence bundles surface the deleted triples too.
        foreach (var r in ReadResolutions(row))
        {
            if (!r.Op.TryGetValue("op", out var opNameObj) || opNameObj is not string opName) continue;
            if (opName != "delete_axiom") continue;
            if (!r.Op.TryGetValue("type", out var tObj) || tObj is not string t) continue;
            switch (t)
            {
                case "subclass":
                    if (r.Op.TryGetValue("sub", out var subObj) && subObj is string sub
                        && r.Op.TryGetValue("super", out var supObj) && supObj is string sup)
                    {
                        keys.Add($"subClassOf|{IriLocal(sub)}|{IriLocal(sup)}");
                    }
                    break;
                case "disjoint":
                case "equivalent":
                    if (r.Op.TryGetValue("a", out var aObj) && aObj is string a
                        && r.Op.TryGetValue("b", out var bObj) && bObj is string b)
                    {
                        var kind = t == "disjoint" ? "disjointWith" : "equivalentClass";
                        var pair = new[] { IriLocal(a), IriLocal(b) };
                        Array.Sort(pair, StringComparer.Ordinal);
                        keys.Add($"{kind}|{pair[0]}|{pair[1]}");
                    }
                    break;
            }
        }
        return keys;
    }

    private static bool IsNoOpResolution(IReadOnlyDictionary<string, object?> op)
        => op.TryGetValue("op", out var v) && v is string s && s == "noop";

    /// <summary>
    /// Tiny port of <c>provenance.describe_axiom</c> — produces the
    /// human-readable rendering of one <c>axiom_key</c> (e.g.
    /// <c>class|Person</c> &rarr; <c>Person</c>, <c>subClassOf|A|B</c> &rarr;
    /// <c>A ⊑ B</c>). The Python source uses a much larger lookup; this
    /// port covers the keys <see cref="ConflictAxiomKeys"/> actually emits.
    /// </summary>
    private static string DescribeAxiom(string axiomKey, Func<string, string> labelLookup)
    {
        var parts = axiomKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return axiomKey;
        var head = parts[0];
        if (head == "class" && parts.Length >= 2) return labelLookup(parts[1]);
        if (head is "objprop" or "dataprop" && parts.Length >= 2) return labelLookup(parts[1]);
        if ((head == "subClassOf" || head == "disjointWith" || head == "equivalentClass") && parts.Length >= 3)
        {
            var a = labelLookup(parts[1]);
            var b = labelLookup(parts[2]);
            return head switch
            {
                "subClassOf" => $"{a} ⊑ {b}",
                "disjointWith" => $"{a} ⟂ {b}",
                "equivalentClass" => $"{a} ≡ {b}",
                _ => $"{a} {head} {b}",
            };
        }
        if (head is "domain" or "range" && parts.Length >= 3)
        {
            var prop = labelLookup(parts[1]);
            var cls = labelLookup(parts[2]);
            return $"{prop} {head} {cls}";
        }
        return axiomKey;
    }

    private static string IriLocal(string iri)
    {
        var hash = iri.LastIndexOf('#');
        if (hash >= 0 && hash < iri.Length - 1) return iri[(hash + 1)..];
        var slash = iri.LastIndexOf('/');
        if (slash >= 0 && slash < iri.Length - 1) return iri[(slash + 1)..];
        return iri;
    }
}
