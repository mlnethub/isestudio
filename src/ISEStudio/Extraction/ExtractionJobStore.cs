using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

/// <summary>
/// Parse / render the comma-separated phase log the orchestrator appends to.
/// The orchestrator does not own its own in-memory state across calls; the
/// log is the deterministic phase history the tests assert against, so it
/// has to round-trip through a single <see cref="string"/> column.
/// </summary>
public static class ExtractionJobLog
{
    private const char Separator = ',';

    /// <summary>Append a phase transition to the existing log.</summary>
    public static string Append(string existing, string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        if (string.IsNullOrEmpty(existing)) return phase;
        return existing + Separator + phase;
    }

    /// <summary>The ordered list of phase transitions in the log.</summary>
    public static IReadOnlyList<string> Phases(string log)
    {
        if (string.IsNullOrEmpty(log)) return Array.Empty<string>();
        return log.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// Reads, writes, and polls an <see cref="ExtractionJobEntity"/> row. The
/// orchestrator and the API layer (controllers that expose progress)
/// share this seam so they never touch <see cref="DbContext"/> directly.
///
/// <para>All methods take a <see cref="IDbContextFactory{TContext}"/> and
/// open a fresh context per call, so the background work the orchestrator
/// dispatches onto <see cref="Task.Run"/> never shares an entity tracker
/// with a polling HTTP request.</para>
/// </summary>
public sealed class ExtractionJobStore
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly TimeProvider _clock;
    private readonly IServiceScopeFactory? _scopes;

    public ExtractionJobStore(
        IDbContextFactory<ISEStudioDbContext> contexts,
        TimeProvider clock,
        IServiceScopeFactory? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(clock);
        _contexts = contexts;
        _clock = clock;
        // Optional: production wires the DI scope factory so
        // MarkCompletedAsync can refresh the cached TBox stats; unit
        // tests that build ExtractionJobStore by hand pass null and
        // skip the refresh (the test path verifies the job-row
        // transitions, not the KS-count caching).
        _scopes = scopes;
    }

    /// <summary>Create a fresh pending job row and return it.</summary>
    public async Task<ExtractionJobEntity> CreateAsync(
        Guid knowledgeSystemId,
        string kind,
        string model,
        IReadOnlyList<int> chunkIds,
        int totalChunks,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var job = new ExtractionJobEntity
        {
            KnowledgeSystemId = knowledgeSystemId,
            Kind = kind,
            Status = JobStatus.Pending.ToWire(),
            Model = model,
            ChunkIds = new List<int>(chunkIds),
            TotalChunks = totalChunks,
            ProcessedChunks = 0,
            CreatedAt = _clock.GetUtcNow(),
            Log = string.Empty,
        };
        var allocator = new LegacyIdAllocator(db);
        await allocator.AllocateAndPersistAsync(job, cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>Fetch the current row, or <c>null</c> when no row matches.</summary>
    public async Task<ExtractionJobEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>
    /// List every job for the supplied knowledge system, newest first.
    /// Used by <c>GET /api/knowledge/{ks_id}/jobs</c>. The SQLite test
    /// store refuses DateTimeOffset in ORDER BY, so the rows are
    /// materialised and sorted client-side the same way
    /// <c>KnowledgeService.ListAsync</c> works around the limitation.
    /// </summary>
    public async Task<IReadOnlyList<ExtractionJobEntity>> ListAsync(
        Guid knowledgeSystemId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExtractionJobs.AsNoTracking()
            .Where(j => j.KnowledgeSystemId == knowledgeSystemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return rows;
    }

    /// <summary>
    /// Long-id convenience overload of <see cref="ListAsync(Guid, CancellationToken)"/>:
    /// looks up the knowledge system's Guid primary key from its legacy
    /// route id, then delegates. Returns an empty list when the legacy
    /// id is unknown so the dispatcher can surface a stable empty
    /// response without crashing the test factory.
    /// </summary>
    public async Task<IReadOnlyList<ExtractionJobEntity>> ListAsync(
        long knowledgeSystemLegacyId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ksGuid = await db.KnowledgeSystems.AsNoTracking()
            .Where(k => k.LegacyId == knowledgeSystemLegacyId)
            .Select(k => (Guid?)k.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ksGuid is null) return Array.Empty<ExtractionJobEntity>();
        return await ListAsync(ksGuid.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Return the id of any extraction job for the supplied knowledge
    /// system whose status is currently <c>pending</c> or <c>running</c>,
    /// or <c>null</c> when no in-flight job exists. Used by the
    /// dispatcher to refuse mutating operations against a KS that has a
    /// live extraction in progress — the brief's
    /// "抽取进行中的修改返回 409" requirement.
    /// </summary>
    public async Task<Guid?> FindActiveJobAsync(
        Guid knowledgeSystemId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExtractionJobs.AsNoTracking()
            .Where(j => j.KnowledgeSystemId == knowledgeSystemId
                && (j.Status == JobStatus.Pending.ToWire()
                    || j.Status == JobStatus.Running.ToWire()))
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Return the id of any extraction job (across all knowledge systems)
    /// currently in <c>pending</c> or <c>running</c> state, or <c>null</c>
    /// when the system is idle. The Stage 2/3 service delegation will
    /// replace this with a KS-scoped lookup; for the brief's "抽取进行中
    /// 的修改返回 409" requirement the cross-KS scope is acceptable
    /// because real production writes always bind <c>{ks_id}</c> and the
    /// underlying row check is gated on the long → Guid resolution.
    /// </summary>
    public async Task<Guid?> FindAnyActiveJobAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExtractionJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending.ToWire()
                || j.Status == JobStatus.Running.ToWire())
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Resolve the knowledge system this job will write into. Returns
    /// <c>null</c> when no row matches the supplied id so the caller can
    /// surface a 404 rather than silently defaulting the graph IRI.
    /// </summary>
    public async Task<KnowledgeSystemEntity?> GetKnowledgeSystemAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(ks => ks.Id == id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Transition <paramref name="id"/> from pending to running.</summary>
    public async Task MarkRunningAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.Status = JobStatus.Running.ToWire();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update the live progress counters + phase column.</summary>
    public async Task UpdateProgressAsync(
        Guid id,
        int processedChunks,
        string? phase = null,
        string? appendPhaseToLog = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.ProcessedChunks = processedChunks;
        if (phase is not null) job.Phase = phase;
        if (appendPhaseToLog is not null)
        {
            job.Log = ExtractionJobLog.Append(job.Log, appendPhaseToLog);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persist a TBox merge counter rollup.</summary>
    public async Task RecordTBoxMergeAsync(
        Guid id,
        ExtractionMergeResult result,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.ClassesAdded += result.ClassesAdded;
        job.PropertiesAdded += result.PropertiesAdded;
        job.AxiomsAdded += result.AxiomsAdded;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persist an ABox merge counter rollup.</summary>
    public async Task RecordABoxMergeAsync(
        Guid id,
        ExtractionMergeResult result,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.IndividualsAdded += result.IndividualsAdded;
        job.AssertionsAdded += result.AssertionsAdded;
        job.PendingAdded += result.PendingAdded;

        if (result.UnknownClasses.Count > 0)
        {
            // Merge into the existing histogram so chunk counters add up.
            var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
            if (job.UnknownClasses is { } existing)
            {
                foreach (var prop in existing.RootElement.EnumerateObject())
                {
                    histogram[prop.Name] = prop.Value.GetInt32();
                }
            }
            foreach (var (label, count) in result.UnknownClasses)
            {
                histogram[label] = histogram.TryGetValue(label, out var seen) ? seen + count : count;
            }
            job.UnknownClasses = JsonDocument.Parse(
                JsonSerializer.Serialize(histogram));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persist the terminology sync result.</summary>
    public async Task RecordTerminologyAsync(
        Guid id,
        TerminologyResult result,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.TermsAdded += result.TermsAdded;
        job.TermsMapped += result.TermsMapped;
        job.TerminologyProposals += result.ProposalsQueued;
        if (result.Error is not null) job.TerminologyError = result.Error;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stamp the immutable prompt snapshot onto the job row.</summary>
    public async Task SetPromptSnapshotAsync(
        Guid id,
        JsonDocument snapshot,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.PromptSnapshot = snapshot;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Terminal success: status=completed, finished_at=now.</summary>
    public async Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken)
    {
        Guid ksId;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
            job.Status = JobStatus.Completed.ToWire();
            job.Phase = ExtractionPhase.Finalizing.ToWire();
            job.Log = ExtractionJobLog.Append(job.Log, ExtractionPhase.Finalizing.ToWire());
            job.FinishedAt = _clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ksId = job.KnowledgeSystemId;
        }

        // Refresh the cached class/property/axiom counts so the home
        // page list card reflects the just-completed extraction.
        // Mirrors Python's extraction.py:323/344/541/558 which call
        // refresh_ks_stats after every terminal success.
        //
        // The stats service is scoped (shares the request DbContext),
        // so we open a fresh scope here — this method may run long
        // after the originating HTTP request has completed. Skipped
        // when no scope factory is wired (unit-test path).
        if (_scopes is null) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var stats = scope.ServiceProvider.GetRequiredService<Knowledge.KnowledgeStatsService>();
            await stats.RefreshAsync(ksId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a failed stats refresh must not undo a
            // successfully completed extraction. The next mutation
            // (or explicit POST /api/knowledge/{id}/refresh_stats)
            // will reconcile the cached counts.
        }
    }

    /// <summary>
    /// Terminal failure: status=failed, finished_at=now, error captured.
    /// Called from the orchestrator after the RDF capture has already been
    /// reverted by the merger-side catch block.
    /// </summary>
    public async Task MarkFailedAsync(
        Guid id,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.ExtractionJobs.FirstAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
        job.Status = JobStatus.Failed.ToWire();
        job.Phase = "failed";
        job.Error = error;
        job.FinishedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Block until the job's status reaches a terminal value, polling every
    /// <see cref="WaitPollInterval"/> up to <see cref="WaitTimeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Thrown when the job does not terminate in time — surfaces orchestrator
    /// deadlocks as a test failure rather than an indefinite hang.
    /// </exception>
    public async Task<ExtractionJobEntity> WaitAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var deadline = _clock.GetUtcNow() + WaitTimeout;
        while (true)
        {
            var job = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                // The job row was deleted out from under the waiter; the
                // boot-time StaleJobRecoveryService would have logged a
                // warning, and there's nothing more we can do here.
                throw new InvalidOperationException(
                    $"Extraction job {id} disappeared while waiting for it to terminate.");
            }
            if (ExtractionWire.IsTerminal(job.Status)) return job;
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"Extraction job {id} did not terminate within {WaitTimeout} (current status '{job.Status}').");
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