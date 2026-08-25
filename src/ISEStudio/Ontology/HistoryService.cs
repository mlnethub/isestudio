using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Audit;
using ISEStudio.Authorization;
using ISEStudio.Conflicts;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;

namespace ISEStudio.Ontology;

public sealed class HistoryService
{
    private readonly ISEStudioDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly StoreWrapper _store;
    private readonly AuditLogService _audit;
    private readonly OntologyService _ontology;
    private readonly ConflictService _conflicts;
    private readonly KnowledgeStatsService _stats;

    public HistoryService(ISEStudioDbContext db, KnowledgeSystemAccessService access, StoreWrapper store,
        AuditLogService audit, OntologyService ontology, ConflictService conflicts, KnowledgeStatsService stats)
    { _db = db; _access = access; _store = store; _audit = audit; _ontology = ontology; _conflicts = conflicts; _stats = stats; }

    public async Task<HistoryResponseOut?> ListHistoryAsync(
        Guid ksId, Actor actor, string? category, string? q, int limit, int offset, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer) throw new InvalidOperationException("Viewer access required for history.");

        limit = Math.Clamp(limit, 1, 200);
        var query = _db.AuditEvents.AsNoTracking().Where(e => e.KnowledgeSystemId == ksId);
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => EF.Functions.Like(e.Action, category + ".%"));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().ToLower() + "%";
            query = query.Where(e => EF.Functions.Like(e.Summary.ToLower(), like) || EF.Functions.Like(e.ActorName.ToLower(), like));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        // SQLite 不支持 DateTimeOffset 在 ORDER BY;LegacyId(long)单调递增,
        // 等价 Python 的 `created_at desc, id desc` tiebreak,且跨 provider 可翻译。
        var items = await query.OrderByDescending(e => e.LegacyId)
            .Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        return new HistoryResponseOut(
            items.Select(e => new HistoryItemOut(
                e.Id, e.ActorName, e.Action, e.Summary, e.Detail?.RootElement, e.CreatedAt,
                e.Added is not null || e.Removed is not null)).ToList(),
            total);
    }

    public async Task<RollbackResponseOut?> RollbackAsync(Guid ksId, Guid eventId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor) throw new InvalidOperationException("Editor access required for rollback.");

        var target = await _db.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.KnowledgeSystemId == ksId, ct).ConfigureAwait(false);
        if (target is null) throw new KeyNotFoundException("History event not found");
        if ((target.Added is null || target.Added.Length == 0) && (target.Removed is null || target.Removed.Length == 0))
            throw new InvalidOperationException("This event did not change the ontology, nothing to roll back");

        // group cutoff = group 内最小 LegacyId,否则事件自身 LegacyId
        long cutoffLegacy;
        if (!string.IsNullOrEmpty(target.GroupId))
        {
            var grp = await _db.AuditEvents.AsNoTracking()
                .Where(e => e.KnowledgeSystemId == ksId && e.GroupId == target.GroupId).ToListAsync(ct).ConfigureAwait(false);
            cutoffLegacy = grp.Count == 0 ? target.LegacyId : grp.Min(e => e.LegacyId);
        }
        else cutoffLegacy = target.LegacyId;

        var events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.KnowledgeSystemId == ksId && e.LegacyId >= cutoffLegacy
                && (e.Added != null || e.Removed != null))
            .OrderByDescending(e => e.LegacyId).ToListAsync(ct).ConfigureAwait(false);

        var graphs = events.Select(e => e.Graph ?? ks.GraphIri).Distinct().ToList();
        var rbGid = graphs.Count > 1 ? Guid.NewGuid().ToString("N") : null;
        int undone = 0; bool tboxChanged = false;
        var detail = new Dictionary<string, object?> { ["target_event_id"] = eventId.ToString(), ["cutoff"] = cutoffLegacy };
        var summary = $"Rolled back to before #{cutoffLegacy}" + (target.GroupId is not null ? " (incl. cascaded instances)" : "");

        foreach (var g in graphs)
        {
            var gName = new Oxigraph.NamedNode(g);
            await using var capture = await _store.CaptureAsync(gName, revertOnError: false, cancellationToken: ct).ConfigureAwait(false);
            byte[] pre;
            byte[] post = Array.Empty<byte>();
            byte[] added;
            byte[] removed;
            try
            {
                pre = _store.DumpNQuads(gName);
                foreach (var ev in events)
                {
                    if ((ev.Graph ?? ks.GraphIri) != g) continue;
                    if (ev.Added is not null && ev.Added.Length > 0)
                        _store.RemoveQuads(gName, StoreWrapper.ParseNQuads(ev.Added));
                    if (ev.Removed is not null && ev.Removed.Length > 0)
                        _store.AddQuads(gName, StoreWrapper.ParseNQuads(ev.Removed));
                    undone++;
                }
                post = _store.DumpNQuads(gName);
            }
            catch
            {
                capture.MarkError();  // 回滚本图的局部写入到 capture 快照
                throw;
            }
            (added, removed) = StoreWrapper.DiffNQuads(pre, post);
            if (added.Length == 0 && removed.Length == 0) continue;
            if (g == ks.GraphIri) tboxChanged = true;
            await _audit.RecordAsync(ksId, user, "system.rollback", summary, detail, g, added, removed, rbGid, ct).ConfigureAwait(false);
        }

        object? openConflicts = Array.Empty<object>();
        if (tboxChanged)
        {
            await _stats.RefreshAsync(ksId, ct).ConfigureAwait(false);
            openConflicts = await _conflicts.SyncAfterOntologyMutationAsync(ksId, semantic: false, ct).ConfigureAwait(false);
        }
        var view = await _ontology.GetViewAsync(ksId, actor, ct).ConfigureAwait(false);
        return new RollbackResponseOut(undone, view, openConflicts);
    }

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> ResolveUserAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userGuid, ct).ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return (null, null);
        return (user, ks);
    }
}
