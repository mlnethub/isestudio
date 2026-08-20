using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

/// <summary>
/// Write-side surface for the <see cref="AboxProvenanceEntity"/> table. Mirrors
/// the Python <c>backend/app/ontology/statement_provenance.py::record_abox_fact</c>
/// / <c>remove_abox_facts</c> pair so manual ABox edits (the B7b
/// <c>abox.add_assertion</c> / <c>abox.remove_assertion</c> paths) leave the
/// same canonical-key provenance rows the extraction pipeline writes.
///
/// <para>The read-side <c>sources_for(...)</c> lookup &mdash; which joins
/// <c>AboxProvenance</c> rows against chunks / jobs / prompts to fill the
/// <c>sources</c> arrays on the <see cref="IndividualOut"/> response &mdash;
/// lands in a later slice; B7a deliberately ships the assertions CRUD with
/// empty <c>Sources</c> arrays as a placeholder so the wire shape stays
/// stable while the read join is wired.</para>
/// </summary>
public sealed class ABoxProvenanceService
{
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;

    public ABoxProvenanceService(
        OnToPilotDbContext db,
        TimeProvider clock,
        LegacyIdAllocator allocator)
    {
        _db = db;
        _clock = clock;
        _allocator = allocator;
    }

    /// <summary>
    /// Upsert one provenance row keyed by <c>(KnowledgeSystemId, FactKey)</c>.
    /// Matches the Python <c>record_abox_fact</c> contract: when the row
    /// already exists, refresh <c>AuditEventId</c>, <c>ActorName</c>, and
    /// <c>CreatedAt</c> so the most-recent writer wins. <paramref name="method"/>
    /// distinguishes manual edits (<c>"manual"</c>) from extraction writes
    /// (<c>"extraction"</c>).
    /// </summary>
    public async Task RecordFactAsync(
        Guid ksId,
        string factKey,
        Guid auditEventId,
        string method,
        string actorName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(factKey);
        ArgumentException.ThrowIfNullOrEmpty(method);

        var existing = await _db.AboxProvenances
            .FirstOrDefaultAsync(p => p.KnowledgeSystemId == ksId && p.FactKey == factKey, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.AuditEventId = auditEventId;
            existing.Method = method;
            existing.ActorName = actorName;
            existing.CreatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Atomic alloc+save: holds the abox_provenance advisory lock
            // until COMMIT so concurrent WriteAboxProvenanceAsync calls on
            // distinct (ksId, factKey) pairs can't observe the same MAX+1
            // and race on the UNIQUE(legacy_id) constraint. SQLite takes
            // the autocommit path because single-writer mode already
            // serialises INSERTs at the database layer.
            await _allocator.AllocateAndPersistAsync(new AboxProvenanceEntity
            {
                KnowledgeSystemId = ksId,
                FactKey = factKey,
                Method = method,
                ActorName = actorName,
                AuditEventId = auditEventId,
                CreatedAt = _clock.GetUtcNow(),
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delete every <see cref="AboxProvenanceEntity"/> row matching
    /// <c>(KnowledgeSystemId, FactKey)</c>. Mirrors the Python
    /// <c>remove_abox_facts(session, ks.id, {key})</c> call.
    /// </summary>
    public async Task RemoveFactsAsync(Guid ksId, string factKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(factKey);
        var rows = await _db.AboxProvenances
            .Where(p => p.KnowledgeSystemId == ksId && p.FactKey == factKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return;
        _db.AboxProvenances.RemoveRange(rows);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>DI helper for the ABox provenance service registration.</summary>
public static class ABoxProvenanceServiceCollectionExtensions
{
    public static IServiceCollection AddAboxProvenanceServices(this IServiceCollection services)
    {
        services.AddScoped<ABoxProvenanceService>();
        return services;
    }
}