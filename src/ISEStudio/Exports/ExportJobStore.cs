using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Exports;

/// <summary>
/// Reads, writes, and polls an <see cref="ExportJobEntity"/> row. The
/// background runner (<see cref="ExportRunner"/>) and the API layer
/// (controllers + dispatcher arms) share this seam so they never touch
/// <see cref="DbContext"/> directly.
///
/// <para>All methods take a <see cref="IDbContextFactory{TContext}"/> and
/// open a fresh context per call, so the background work the runner
/// dispatches onto <see cref="Task.Run"/> never shares an entity tracker
/// with a polling HTTP request — mirrors the
/// <see cref="Extraction.ExtractionJobStore"/> contract exactly.</para>
/// </summary>
public sealed class ExportJobStore
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly TimeProvider _clock;

    public ExportJobStore(
        IDbContextFactory<ISEStudioDbContext> contexts,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(clock);
        _contexts = contexts;
        _clock = clock;
    }

    /// <summary>
    /// Insert a fresh pending job row and return it. The row gets a fresh
    /// Guid Id (Phase 3 retired the legacy_id column and its UNIQUE indexes).
    /// </summary>
    public async Task<ExportJobEntity> CreateAsync(
        Guid knowledgeSystemId,
        Guid? releaseId,
        string layer,
        int shardSize,
        string format,
        Guid? createdById,
        string createdByName,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = new ExportJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = knowledgeSystemId,
            ReleaseId = releaseId,
            Layer = layer,
            Format = format,
            Status = "pending",
            ShardSize = shardSize,
            ProcessedStatements = 0,
            TotalStatements = 0,
            OutputDir = string.Empty,
            CreatedById = createdById,
            CreatedByName = string.IsNullOrEmpty(createdByName) ? "system" : createdByName,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.ExportJobs.Add(row);
        await db.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        return row;
    }

    /// <summary>Fetch the current row by <see cref="ExportJobEntity.Id"/>, or <c>null</c>.</summary>
    public async Task<ExportJobEntity?> GetAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.ExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a job row from its Guid primary key. Phase 3 retired the
    /// legacy_id column, so the resourceId is parsed only as a GUID.
    /// Returns <c>null</c> when the string is not a GUID or when no row
    /// matches so the dispatcher can surface a stable 404.
    /// </summary>
    public async Task<ExportJobEntity?> ResolveAsync(
        ISEStudioDbContext db,
        Guid knowledgeSystemId,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        if (!Guid.TryParse(resourceId, out var guid)) return null;
        return await db.ExportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.KnowledgeSystemId == knowledgeSystemId
                && j.Id == guid, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every job for the supplied knowledge system, newest first. Phase 3:
    /// legacy_id 列已退役, ordering rides on CreatedAt (Python parity).
    /// </summary>
    public async Task<IReadOnlyList<ExportJobEntity>> ListAsync(
        Guid knowledgeSystemId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.ExportJobs.AsNoTracking()
            .Where(j => j.KnowledgeSystemId == knowledgeSystemId)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Transition <paramref name="id"/> from pending to running.</summary>
    public async Task MarkRunningAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ExportJobs.FirstAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
        row.Status = "running";
        row.StartedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update the live processed-statements counter.</summary>
    public async Task UpdateProgressAsync(
        Guid id, int processedStatements, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ExportJobs.FirstAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
        row.ProcessedStatements = processedStatements;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persist the descriptor list returned by
    /// <see cref="ExportArtifactStore"/>. Serialised as a JSON array so the
    /// wire shape (name/layer/statements/bytes/sha256) matches the
    /// <see cref="ExportFileEntry"/> DTO.
    /// </summary>
    public async Task RecordFilesAsync(
        Guid id,
        IReadOnlyList<ExportFileEntry> files,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ExportJobs.FirstAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
        // Snake-case the descriptor list so the persisted JSON matches
        // the ExportOut wire shape (which the global
        // JsonNamingPolicy.SnakeCaseLower policy hands the frontend).
        // Keeps DownloadFileAsync's filename lookup (`"name"` key) and
        // ProjectToOut's deserialisation round-trip aligned.
        row.Files = JsonDocument.Parse(JsonSerializer.Serialize(
            files, WireJson.Options));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
    }

    /// <summary>Terminal success: status=completed, finished_at=now.</summary>
    public async Task MarkCompletedAsync(
        Guid id, int totalStatements, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ExportJobs.FirstAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
        row.Status = "completed";
        row.TotalStatements = totalStatements;
        row.ProcessedStatements = totalStatements;
        row.FinishedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Terminal failure: status=failed, finished_at=now, error captured.</summary>
    public async Task MarkFailedAsync(
        Guid id, string error, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ExportJobs.FirstAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
        row.Status = "failed";
        row.Error = error;
        row.FinishedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Block until the job reaches a terminal status (completed / failed),
    /// polling every <see cref="WaitPollInterval"/> up to
    /// <see cref="WaitTimeout"/>. Mirrors
    /// <see cref="Extraction.ExtractionJobStore.WaitAsync"/>: a test
    /// timeout surfaces as <see cref="TimeoutException"/> rather than
    /// hanging the test process.
    /// </summary>
    public async Task<ExportJobEntity> WaitAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var deadline = _clock.GetUtcNow() + WaitTimeout;
        while (true)
        {
            var job = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                throw new InvalidOperationException(
                    $"Export job {id} disappeared while waiting for it to terminate.");
            }
            if (job.Status is "completed" or "failed") return job;
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"Export job {id} did not terminate within {WaitTimeout} (current status '{job.Status}').");
            }
            try
            {
                await Task.Delay(WaitPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}