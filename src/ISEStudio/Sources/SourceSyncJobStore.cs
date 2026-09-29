using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Runtime.CompilerServices;

namespace ISEStudio.Sources;

public sealed record SourceSyncResult(
    int Added,
    int Updated,
    IReadOnlyList<string> Errors,
    bool IsComplete);

public sealed class SourceSyncJobStore
{
    private const string Queued = "queued";
    private const string Running = "running";
    private const string Ok = "ok";
    private const string Failed = "failed";
    private const int MaxRunsPerSource = 50;
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly ConditionalWeakTable<IDbContextTransaction, HashSet<ClaimKey>>
        ValidatedClaims = new();

    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseDuration;

    public SourceSyncJobStore(
        IDbContextFactory<ISEStudioDbContext> contexts,
        TimeProvider clock,
        TimeSpan? leaseDuration = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    public async Task<Guid> EnqueueAsync(Guid sourceId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var source = await LockSourceAsync(db, sourceId, ct).ConfigureAwait(false);
        if (source is null) throw new InvalidOperationException("Source not found.");

        var existing = await db.SourceSyncJobs.AsNoTracking()
            .Where(job => job.SourceId == sourceId && (job.Status == Queued || job.Status == Running))
            .Select(job => (Guid?)job.Id)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing.HasValue)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return existing.Value;
        }

        var job = new SourceSyncJobEntity
        {
            SourceId = sourceId,
            Status = Queued,
            CreatedAt = _clock.GetUtcNow(),
        };
        source.LastSyncStatus = Queued;
        source.LastSyncError = null;
        db.SourceSyncJobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return job.Id;
    }

    public async Task<SourceSyncJobEntity?> ClaimNextAsync(CancellationToken ct)
    {
        await RecoverExpiredAsync(ct).ConfigureAwait(false);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);

        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var sourceId = await LockNextQueuedSourceAsync(db, ct).ConfigureAwait(false);
            if (!sourceId.HasValue)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return null;
            }

            var source = await LockSourceAsync(db, sourceId.Value, ct).ConfigureAwait(false);
            if (source is null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var job = await LockQueuedJobAsync(db, sourceId.Value, ct).ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var now = _clock.GetUtcNow();
            var run = new SourceSyncRunEntity
            {
                SourceId = sourceId.Value,
                Status = Running,
                StartedAt = now,
            };
            db.SourceSyncRuns.Add(run);
            job.Status = Running;
            job.ActiveRunId = run.Id;
            job.LeaseUntil = now + _leaseDuration;
            job.StartedAt = now;
            job.FinishedAt = null;
            job.Error = null;
            source.LastSyncStatus = Running;
            source.LastSyncError = null;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return job;
        }
    }

    public async Task<bool> RenewAsync(Guid jobId, Guid runId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var sourceId = await db.SourceSyncJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => (Guid?)job.SourceId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!sourceId.HasValue)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        var source = await LockSourceAsync(db, sourceId.Value, ct).ConfigureAwait(false);
        if (source is null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }
        var job = await LockJobAsync(db, jobId, sourceId.Value, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (job is null || job.Status != Running || job.ActiveRunId != runId
            || job.LeaseUntil is null || job.LeaseUntil <= now)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }
        var run = await db.SourceSyncRuns.SingleOrDefaultAsync(item => item.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null || run.SourceId != sourceId.Value || run.Status != Running)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        job.LeaseUntil = now + _leaseDuration;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ValidateClaimUnderLockAsync(
        ISEStudioDbContext db, Guid sourceId, Guid jobId, Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var transaction = RequireTransaction(db);
        var source = await LockSourceAsync(db, sourceId, ct).ConfigureAwait(false);
        if (source is null) return false;
        var job = await LockJobAsync(db, jobId, sourceId, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (job is null || job.SourceId != sourceId || job.Status != Running
            || job.ActiveRunId != runId || job.LeaseUntil is null || job.LeaseUntil <= now)
            return false;
        var run = await db.SourceSyncRuns.SingleOrDefaultAsync(item => item.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null || run.SourceId != sourceId || run.Status != Running)
            return false;

        var validations = ValidatedClaims.GetValue(transaction, _ => []);
        lock (validations) validations.Add(new ClaimKey(sourceId, jobId, runId));
        return true;
    }

    public async Task<bool> RenewUnderLockAsync(
        ISEStudioDbContext db, Guid sourceId, Guid jobId, Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var transaction = RequireTransaction(db);
        var validations = ValidatedClaims.GetValue(transaction, _ => []);
        lock (validations)
        {
            if (!validations.Contains(new ClaimKey(sourceId, jobId, runId))) return false;
        }

        var source = await LockSourceAsync(db, sourceId, ct).ConfigureAwait(false);
        if (source is null) return false;
        var job = await LockJobAsync(db, jobId, sourceId, ct).ConfigureAwait(false);
        if (job is null || job.SourceId != sourceId || job.Status != Running || job.ActiveRunId != runId)
            return false;
        var run = await db.SourceSyncRuns.SingleOrDefaultAsync(item => item.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null || run.SourceId != sourceId || run.Status != Running) return false;

        job.LeaseUntil = _clock.GetUtcNow() + _leaseDuration;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task CompleteAsync(
        Guid jobId, Guid runId, SourceSyncResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var sourceId = await db.SourceSyncJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => (Guid?)job.SourceId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!sourceId.HasValue)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var source = await LockSourceAsync(db, sourceId.Value, ct).ConfigureAwait(false);
        if (source is null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        var job = await LockJobAsync(db, jobId, sourceId.Value, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (job is null || job.Status != Running || job.ActiveRunId != runId
            || job.LeaseUntil is null || job.LeaseUntil <= now)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        var run = await db.SourceSyncRuns.SingleOrDefaultAsync(item => item.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null || run.SourceId != sourceId.Value || run.Status != Running)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var success = result.IsComplete && result.Errors.Count == 0;
        var status = success ? Ok : Failed;
        var error = result.Errors.Count == 0
            ? (result.IsComplete ? null : "Source scan was incomplete.")
            : string.Join(Environment.NewLine, result.Errors);
        run.Status = status;
        run.FinishedAt = now;
        run.AddedCount = result.Added;
        run.UpdatedCount = result.Updated;
        run.Error = error;
        job.Status = status;
        job.ActiveRunId = null;
        job.LeaseUntil = null;
        job.FinishedAt = now;
        job.Error = error;
        source.LastSyncedAt = now;
        source.LastSyncStatus = status;
        source.LastSyncError = error;
        source.LastSyncAdded = result.Added;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        List<Guid> expiredRunIds;
        if (db.Database.IsNpgsql())
        {
            expiredRunIds = await db.SourceSyncRuns.AsNoTracking()
                .Where(item => item.SourceId == sourceId.Value)
                .OrderByDescending(item => item.StartedAt)
                .ThenByDescending(item => item.Id)
                .Skip(MaxRunsPerSource)
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var sourceRuns = await db.SourceSyncRuns.AsNoTracking()
                .Where(item => item.SourceId == sourceId.Value)
                .ToListAsync(ct).ConfigureAwait(false);
            expiredRunIds = sourceRuns
                .OrderByDescending(item => item.StartedAt)
                .ThenByDescending(item => item.Id)
                .Skip(MaxRunsPerSource)
                .Select(item => item.Id)
                .ToList();
        }
        if (expiredRunIds.Count > 0)
        {
            var expiredRuns = await db.SourceSyncRuns
                .Where(item => expiredRunIds.Contains(item.Id))
                .ToListAsync(ct).ConfigureAwait(false);
            db.SourceSyncRuns.RemoveRange(expiredRuns);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> RecoverExpiredAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        List<Guid> sourceIds;
        if (db.Database.IsNpgsql())
        {
            sourceIds = await db.SourceSyncJobs.AsNoTracking()
                .Where(job => job.Status == Running && job.LeaseUntil <= now)
                .OrderBy(job => job.CreatedAt)
                .Select(job => job.SourceId)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var runningJobs = await db.SourceSyncJobs.AsNoTracking()
                .Where(job => job.Status == Running)
                .Select(job => new { job.SourceId, job.LeaseUntil })
                .ToListAsync(ct).ConfigureAwait(false);
            sourceIds = runningJobs
                .Where(job => job.LeaseUntil <= now)
                .Select(job => job.SourceId)
                .Distinct()
                .ToList();
        }
        var recovered = 0;

        foreach (var sourceId in sourceIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var source = await LockSourceAsync(db, sourceId, ct).ConfigureAwait(false);
            if (source is null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                continue;
            }
            var runningJobs = await db.SourceSyncJobs
                .Where(item => item.SourceId == sourceId && item.Status == Running)
                .ToListAsync(ct).ConfigureAwait(false);
            var job = runningJobs.OrderBy(item => item.CreatedAt).FirstOrDefault();
            now = _clock.GetUtcNow();
            if (job is null || job.LeaseUntil is null || job.LeaseUntil > now || !job.ActiveRunId.HasValue)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var run = await db.SourceSyncRuns.SingleOrDefaultAsync(item => item.Id == job.ActiveRunId, ct)
                .ConfigureAwait(false);
            if (run is not null && run.Status == Running)
            {
                run.Status = Failed;
                run.FinishedAt = now;
                run.Error = "Source sync lease expired.";
            }
            job.Status = Queued;
            job.ActiveRunId = null;
            job.LeaseUntil = null;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.Error = null;
            source.LastSyncStatus = Queued;
            source.LastSyncError = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            recovered++;
        }

        return recovered;
    }

    private static async Task<Guid?> LockNextQueuedSourceAsync(ISEStudioDbContext db, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Database.SqlQuery<Guid?>($"""
                    SELECT s.id AS "Value"
                    FROM source AS s
                    INNER JOIN source_sync_job AS job ON job.source_id = s.id
                    WHERE job.status = {Queued}
                    ORDER BY job.created_at, job.id
                    LIMIT 1
                    FOR UPDATE OF s SKIP LOCKED
                    """)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        var queuedJobs = await db.SourceSyncJobs.AsNoTracking()
            .Where(job => job.Status == Queued)
            .Select(job => new { job.SourceId, job.CreatedAt, job.Id })
            .ToListAsync(ct).ConfigureAwait(false);
        return queuedJobs.OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Select(job => (Guid?)job.SourceId)
            .FirstOrDefault();
    }

    private static async Task<SourceEntity?> LockSourceAsync(
        ISEStudioDbContext db, Guid sourceId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Sources.FromSqlInterpolated(
                    $"SELECT * FROM source WHERE id = {sourceId} FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE source SET id = id WHERE id = {sourceId}", ct).ConfigureAwait(false);
        return await db.Sources.SingleOrDefaultAsync(source => source.Id == sourceId, ct)
            .ConfigureAwait(false);
    }

    private static async Task<SourceSyncJobEntity?> LockQueuedJobAsync(
        ISEStudioDbContext db, Guid sourceId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.SourceSyncJobs.FromSqlInterpolated(
                    $"SELECT * FROM source_sync_job WHERE source_id = {sourceId} AND status = {Queued} ORDER BY created_at, id LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
        var queuedJobs = await db.SourceSyncJobs
            .Where(job => job.SourceId == sourceId && job.Status == Queued)
            .ToListAsync(ct).ConfigureAwait(false);
        return queuedJobs.OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .FirstOrDefault();
    }

    private static async Task<SourceSyncJobEntity?> LockJobAsync(
        ISEStudioDbContext db, Guid jobId, Guid sourceId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.SourceSyncJobs.FromSqlInterpolated(
                    $"SELECT * FROM source_sync_job WHERE id = {jobId} AND source_id = {sourceId} FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
        return await db.SourceSyncJobs.SingleOrDefaultAsync(
                job => job.Id == jobId && job.SourceId == sourceId, ct)
            .ConfigureAwait(false);
    }

    private static IDbContextTransaction RequireTransaction(ISEStudioDbContext db)
        => db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A caller-owned database transaction is required.");

    private readonly record struct ClaimKey(Guid SourceId, Guid JobId, Guid RunId);
}