using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Infrastructure.Startup;

/// <summary>
/// Boot-time backfill for documents that were uploaded before the knowledge
/// system existed (or before they could be bound to one) and therefore have
/// <c>KnowledgeSystemId = NULL</c>. Mirrors the Python backend's
/// <c>_backfill_document_ks</c> lifespan step.
/// </summary>
/// <remarks>
/// <para>Heuristic: an orphan with axiom provenance is bound to the KS its
/// chunks contributed the most axioms to. An orphan with no provenance
/// falls back to the earliest KS so the document stays visible. Orphans
/// with neither fall back to the earliest KS, or remain unbound if no KS
/// exists yet.</para>
/// <para>Safe to re-run: the WHERE clause filters on
/// <c>KnowledgeSystemId == NULL</c>, so already-bound rows are skipped.</para>
/// </remarks>
public sealed class LegacyBackfillService
{
    private readonly OnToPilotDbContext _db;
    private readonly ILogger<LegacyBackfillService> _logger;

    /// <summary>DI constructor.</summary>
    public LegacyBackfillService(
        OnToPilotDbContext db,
        ILogger<LegacyBackfillService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Bind every <c>KnowledgeSystemId IS NULL</c> document to a knowledge
    /// system using provenance (preferred) or first-KS fallback. Returns
    /// the number of rows that were updated.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var orphans = await _db.Documents
            .Where(d => d.KnowledgeSystemId == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (orphans.Count == 0) return 0;

        var firstKs = await _db.KnowledgeSystems
            .OrderBy(k => k.Id)
            .Select(k => (Guid?)k.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var bound = 0;
        foreach (var doc in orphans)
        {
            Guid? ksId = null;

            // Prefer: the KS that consumed the most axioms from this doc's chunks.
            var chunkIds = await _db.Chunks
                .Where(c => c.DocumentId == doc.Id)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (chunkIds.Count > 0)
            {
                var counts = await _db.AxiomProvenances
                    .Where(p => p.ChunkId != null && chunkIds.Contains(p.ChunkId.Value))
                    .GroupBy(p => p.KnowledgeSystemId)
                    .Select(g => new { KsId = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (counts is not null)
                {
                    ksId = counts.KsId;
                }
            }

            // Fallback: bind to the earliest KS so the document is at least visible.
            ksId ??= firstKs;
            if (ksId is null) continue;

            doc.KnowledgeSystemId = ksId;
            bound++;
        }

        if (bound > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "bound {Count} pre-existing document(s) to a knowledge system", bound);
        }

        return bound;
    }
}