using Cronos;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Sources;

public sealed class SourceSyncScheduler
{
    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly SourceAdapterRegistry _registry;
    private readonly TimeProvider _clock;

    public SourceSyncScheduler(
        IDbContextFactory<ISEStudioDbContext> contexts,
        SourceAdapterRegistry registry,
        TimeProvider clock)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<int> ScheduleDueSourcesAsync(CancellationToken cancellationToken)
    {
        await using var lookup = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sourceIds = await lookup.Sources.AsNoTracking()
            .Where(source => source.SyncIntervalMinutes != null || source.SyncCron != null)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var enqueued = 0;

        foreach (var sourceId in sourceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var source = await LockSourceAsync(db, sourceId, cancellationToken).ConfigureAwait(false);
            if (source is null || !_registry.TryGet(source.Kind, out var descriptor) || !descriptor.ActiveSync)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var dueAt = GetDueAt(source, _clock.GetUtcNow().ToUniversalTime());
            if (!dueAt.HasValue)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            source.LastScheduledAt = dueAt.Value;
            var activeJobExists = await db.SourceSyncJobs.AnyAsync(job =>
                    job.SourceId == sourceId && (job.Status == "queued" || job.Status == "running"),
                cancellationToken).ConfigureAwait(false);
            if (!activeJobExists)
            {
                db.SourceSyncJobs.Add(new SourceSyncJobEntity
                {
                    SourceId = sourceId,
                    Status = "queued",
                    CreatedAt = _clock.GetUtcNow(),
                });
                source.LastSyncStatus = "queued";
                source.LastSyncError = null;
                enqueued++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return enqueued;
    }

    private static DateTimeOffset? GetDueAt(SourceEntity source, DateTimeOffset now)
    {
        var baseline = (source.LastScheduledAt ?? source.CreatedAt).ToUniversalTime();
        if (source.SyncIntervalMinutes is > 0)
        {
            var dueAt = baseline.AddMinutes(source.SyncIntervalMinutes.Value);
            return dueAt <= now ? dueAt : null;
        }

        if (string.IsNullOrWhiteSpace(source.SyncCron)) return null;
        try
        {
            var expression = CronExpression.Parse(source.SyncCron, CronFormat.Standard);
            var next = expression.GetNextOccurrence(baseline, TimeZoneInfo.Utc);
            return next.HasValue && next.Value <= now ? next.Value : null;
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    private static async Task<SourceEntity?> LockSourceAsync(
        ISEStudioDbContext db, Guid sourceId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Sources.FromSqlInterpolated(
                    $"SELECT * FROM source WHERE id = {sourceId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE source SET id = id WHERE id = {sourceId}", cancellationToken).ConfigureAwait(false);
        return await db.Sources.SingleOrDefaultAsync(source => source.Id == sourceId, cancellationToken)
            .ConfigureAwait(false);
    }
}