using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;

namespace ISEStudio.Knowledge;

/// <summary>
/// Recomputes the cached <c>class_count / property_count / axiom_count</c>
/// columns on <see cref="KnowledgeSystemEntity"/> from the live TBox
/// graph. Mirrors the Python baseline
/// <c>backend/app/api/knowledge.py::refresh_ks_stats</c> &mdash; the
/// .NET port never updated those columns during extraction / ontology
/// edits, which left the home-page <c>0 类 / 0 属性 / 0 公理</c> badges
/// stale even when the graph actually contained classes, properties,
/// and axioms.
///
/// <para>The TBox stats are derived from the same
/// <see cref="OntologyViewBuilder.BuildFromStoreAsync"/> algorithm that
/// powers <c>GET /api/knowledge/{id}/ontology</c>, so the cached
/// counts are guaranteed to match what the ontology page renders.</para>
///
/// <para>ABox-only changes (individual CRUD, assertion add/remove) do
/// not alter class / property / axiom counts, but Python's
/// <c>refresh_ks_stats</c> is still called after the ABox reset path
/// for parity &mdash; we follow that conservatism so a stale row
/// can't survive a destructive operation.</para>
/// </summary>
public sealed class KnowledgeStatsService : IKnowledgeStatsService
{
    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;
    private readonly StoreWrapper _store;
    private readonly OntologyViewBuilder _builder;

    /// <summary>DI constructor. Scoped lifetime shares the request's
    /// <see cref="ISEStudioDbContext"/>.</summary>
    public KnowledgeStatsService(
        ISEStudioDbContext db,
        TimeProvider clock,
        StoreWrapper store,
        OntologyViewBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(builder);
        _db = db;
        _clock = clock;
        _store = store;
        _builder = builder;
    }

    /// <summary>
    /// Recompute <c>ClassCount / PropertyCount / AxiomCount</c> from
    /// the live TBox graph and persist the new totals to
    /// <paramref name="ksId"/>. No-op when the KS no longer exists.
    /// Mirrors Python <c>refresh_ks_stats(session, ks)</c>.
    /// </summary>
    public async Task RefreshAsync(Guid ksId, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null) return;

        var view = await _builder
            .BuildFromStoreAsync(_store, ks.GraphIri, ct)
            .ConfigureAwait(false);

        // Short-circuit when nothing changed so we don't churn
        // UpdatedAt on every read-only refresh (matches Python's
        // behaviour of always writing updated_at — we tolerate the
        // extra write because the operator path is rare).
        ks.ClassCount = view.Stats.ClassCount;
        ks.PropertyCount = view.Stats.PropertyCount;
        ks.AxiomCount = view.Stats.AxiomCount;
        ks.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
