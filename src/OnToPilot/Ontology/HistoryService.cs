using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Audit;
using OnToPilot.Authorization;
using OnToPilot.Conflicts;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Knowledge;

namespace OnToPilot.Ontology;

public sealed class HistoryService
{
    private readonly OnToPilotDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly StoreWrapper _store;
    private readonly AuditLogService _audit;
    private readonly OntologyService _ontology;
    private readonly ConflictService _conflicts;
    private readonly KnowledgeStatsService _stats;

    public HistoryService(OnToPilotDbContext db, KnowledgeSystemAccessService access, StoreWrapper store,
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
