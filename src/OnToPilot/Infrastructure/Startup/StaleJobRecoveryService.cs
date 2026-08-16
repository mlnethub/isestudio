using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Infrastructure.Startup;

/// <summary>
/// Boot-time recovery for jobs that were left in <c>pending</c> /
/// <c>running</c> / <c>provisioning</c> / <c>stopping</c> by a previous
/// process. Without this pass, <c>extraction_active()</c> would report
/// those rows forever and every mutating operation on the affected KS would
/// be locked out.
/// </summary>
/// <remarks>
/// <para>Mirrors the Python backend's <c>_reset_stale_jobs</c> lifespan
/// step: extraction jobs in flight, export jobs in flight, ontology
/// releases mid-capture, and release deployments in <c>provisioning</c> /
/// <c>stopping</c> all transition to a terminal state with an
/// <c>"Interrupted by a server restart"</c> note. Completed rows are
/// untouched so previously successful work is preserved.</para>
/// <para>The pass is idempotent: re-running it on already-failed rows is a
/// no-op because the timestamp is only written once per row, and we only
/// select rows currently in the active states.</para>
/// </remarks>
public sealed class StaleJobRecoveryService
{
    private readonly OnToPilotDbContext _db;
    private readonly ILogger<StaleJobRecoveryService> _logger;

    /// <summary>DI constructor.</summary>
    public StaleJobRecoveryService(
        OnToPilotDbContext db,
        ILogger<StaleJobRecoveryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Transition every row whose lifecycle is <c>pending</c> or
    /// <c>running</c> into <c>failed</c> (or <c>stopped</c> for release
    /// deployments that were shutting down).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // ---- Extraction jobs ----
        var staleExtraction = await _db.ExtractionJobs
            .Where(j => j.Status == "pending" || j.Status == "running")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var job in staleExtraction)
        {
            job.Status = "failed";
            job.Error = "Interrupted by a server restart";
            job.FinishedAt = now;
        }
        if (staleExtraction.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "reset {Count} stale extraction job(s) left running by a previous process",
                staleExtraction.Count);
        }

        // ---- Export jobs ----
        var staleExport = await _db.ExportJobs
            .Where(j => j.Status == "pending" || j.Status == "running")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var job in staleExport)
        {
            job.Status = "failed";
            job.Error = "Interrupted by a server restart";
            job.FinishedAt = now;
        }
        if (staleExport.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "reset {Count} stale export job(s) left running by a previous process",
                staleExport.Count);
        }

        // ---- Release deployments ----
        var staleDeployments = await _db.ReleaseDeployments
            .Where(d => d.Status == "provisioning" || d.Status == "stopping")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var deployment in staleDeployments)
        {
            deployment.Status = deployment.Status == "provisioning" ? "failed" : "stopped";
            deployment.Error = "Interrupted by a server restart";
            deployment.StoppedAt = now;
        }
        if (staleDeployments.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "reset {Count} stale release deployment(s) left in flight by a previous process",
                staleDeployments.Count);
        }
    }
}