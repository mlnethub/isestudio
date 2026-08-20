using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

/// <summary>
/// Persisted validation-decision store. Mirrors the Python
/// <c>backend/app/ontology/validation_agent.py::record_decision</c>
/// / list / revoke surface so the B7c <c>abox.fix_violation</c>
/// path can record a human's "this numeric property is qualitative"
/// call (action <c>"relax"</c>) and a future
/// <c>ValidationAgent</c> can replay it next triage.
///
/// <para>The Python store keeps <c>(knowledge_system_id, property_iri)</c>
/// as the upsert key. <see cref="RecordDecisionAsync"/> matches: a
/// second call for the same <c>(ks, propertyIri)</c> refreshes
/// <c>Action</c> + <c>XsdType</c> + <c>Reason</c> + <c>ResolvedBy</c> +
/// <c>CreatedAt</c> instead of erroring, so the most-recent caller's
/// preference wins.</para>
///
/// <para><see cref="RevokeAsync"/> returns <c>null</c> when the row
/// doesn't exist (caller funnels to 404); the audit row carries the
/// forget action so history replay can still see the revoke.</para>
/// </summary>
public sealed class ValidationDecisionService
{
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;

    public ValidationDecisionService(
        OnToPilotDbContext db,
        TimeProvider clock,
        LegacyIdAllocator allocator)
    {
        _db = db;
        _clock = clock;
        _allocator = allocator;
    }

    /// <summary>
    /// Persist a decision. <paramref name="action"/> is constrained to
    /// <c>"relax"</c> (relax the property's range to text) or
    /// <c>"remove"</c> (strip the offending data assertion) per the
    /// Python SQLModel. <paramref name="resolvedBy"/> is the actor
    /// name (user) when a human applied the fix, or
    /// <c>"agent"</c> when the future <c>ValidationAgent</c> writes the
    /// row.
    /// </summary>
    public async Task RecordDecisionAsync(
        Guid ksId,
        string? propertyIri,
        string propertyLabel,
        string? xsdType,
        string action,
        string? reason,
        string resolvedBy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);
        ArgumentException.ThrowIfNullOrEmpty(propertyLabel);
        ArgumentException.ThrowIfNullOrEmpty(resolvedBy);

        var normalized = action.Trim().ToLowerInvariant();
        if (normalized != "relax" && normalized != "remove")
        {
            throw new InvalidOperationException(
                $"Unknown validation action '{action}'. Use 'relax' or 'remove'.");
        }

        var now = _clock.GetUtcNow();
        var existing = propertyIri is null
            ? null
            : await _db.ValidationDecisions
                .FirstOrDefaultAsync(
                    d => d.KnowledgeSystemId == ksId && d.PropertyIri == propertyIri, ct)
                .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.PropertyLabel = propertyLabel;
            existing.XsdType = xsdType;
            existing.Action = normalized;
            existing.Reason = reason;
            existing.ResolvedBy = resolvedBy;
            existing.CreatedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Atomic alloc+save: holds the validation_decision advisory
            // lock until COMMIT so concurrent WriteValidationDecisionAsync
            // calls on distinct propertyIris can't observe the same MAX+1
            // and race on the UNIQUE(legacy_id) constraint. SQLite takes
            // the autocommit path because single-writer mode already
            // serialises INSERTs at the database layer.
            await _allocator.AllocateAndPersistAsync(new ValidationDecisionEntity
            {
                KnowledgeSystemId = ksId,
                PropertyIri = propertyIri,
                PropertyLabel = propertyLabel,
                XsdType = xsdType,
                Action = normalized,
                Reason = reason,
                ResolvedBy = resolvedBy,
                CreatedAt = now,
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Page through decisions for one KS, newest first. <paramref name="q"/>
    /// optionally filters by property label substring (case-insensitive).
    /// </summary>
    public async Task<ValidationDecisionListOut> ListDecisionsAsync(
        Guid ksId, string? q, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        var query = _db.ValidationDecisions.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ksId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(d => EF.Functions.Like(d.PropertyLabel, $"%{needle}%"));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        // SQLite refuses DateTimeOffset in ORDER BY, so materialise the
        // rows and sort newest-first client-side (mirrors how
        // ExtractionJobStore.ListAsync and KnowledgeService.ListAsync
        // work around the same limitation).
        var rows = await query
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) =>
        {
            var cmp = b.CreatedAt.CompareTo(a.CreatedAt);
            return cmp != 0 ? cmp : b.LegacyId.CompareTo(a.LegacyId);
        });
        rows = rows.Skip(offset).Take(limit).ToList();

        var items = rows
            .Select(d => new ValidationDecisionOut(
                d.Id, d.PropertyLabel, d.PropertyIri, d.XsdType,
                d.Action, d.Reason, d.ResolvedBy, d.CreatedAt))
            .ToList();
        return new ValidationDecisionListOut(items, total);
    }

    /// <summary>
    /// Forget one decision. Returns the id of the deleted row, or
    /// <c>null</c> when no row matched (the caller can map null → 404).
    /// Cross-KS deletes are prevented by the
    /// <c>knowledge_system_id</c> filter.
    /// </summary>
    public async Task<Guid?> RevokeAsync(Guid ksId, Guid decisionId, CancellationToken ct)
    {
        var row = await _db.ValidationDecisions
            .FirstOrDefaultAsync(
                d => d.Id == decisionId && d.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        _db.ValidationDecisions.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }
}

/// <summary>DI helper for the validation-decision service registration.</summary>
public static class ValidationDecisionServiceCollectionExtensions
{
    public static IServiceCollection AddValidationDecisionServices(this IServiceCollection services)
    {
        services.AddScoped<ValidationDecisionService>();
        return services;
    }
}