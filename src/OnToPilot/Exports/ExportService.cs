using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Exports;

/// <summary>
/// Scoped service facade for the four <c>releases.* exports</c>
/// operations: <c>list_exports</c>, <c>create_export</c>,
/// <c>get_export</c>, <c>download_export_file</c>.
///
/// <para>The create path opens a pending row in the request scope, then
/// dispatches the actual export work onto <see cref="Task.Run"/> with
/// <see cref="ExecutionContext.SuppressFlow"/> so the HTTP response can
/// return <c>status=pending</c> while the runner drains the queue
/// asynchronously. The runner is the singleton <see cref="ExportRunner"/>
/// — same shape as
/// <see cref="Extraction.ExtractionOrchestrator"/>.</para>
///
/// <para>The download path throws <see cref="ExportFilePayloadException"/>
/// (caught by <see cref="FastApiErrorMiddleware"/>) instead of returning
/// the bytes inline, because the middleware short-circuits the JSON
/// envelope and writes the file response directly — mirrors the Python
/// <c>FileResponse(path, media_type=...)</c> shape on
/// <c>backend/app/api/releases.py:759</c>.</para>
/// </summary>
public sealed class ExportService : IDisposable
{
    private readonly OnToPilotDbContext _db;
    private readonly ExportJobStore _jobs;
    private readonly ExportRunner _runner;
    private readonly ExportArtifactStore _artifacts;

    public ExportService(
        OnToPilotDbContext db,
        ExportJobStore jobs,
        ExportRunner runner,
        ExportArtifactStore artifacts)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(artifacts);
        _db = db;
        _jobs = jobs;
        _runner = runner;
        _artifacts = artifacts;
    }

    /// <summary>
    /// Dispose the owned <see cref="OnToPilotDbContext"/>. The DI scope
    /// already covers normal HTTP request lifetime; this is here so the
    /// scoped service can participate in <c>await using</c> from tests
    /// (mirrors the pattern on the other slice services that own a
    /// scoped <see cref="OnToPilotDbContext"/>).
    /// </summary>
    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Every export job for the supplied knowledge system, newest first.
    /// Returns <c>null</c> when the KS is unknown so the dispatcher
    /// degrades to the empty-list placeholder.
    /// </summary>
    public async Task<object?> ListAsync(Guid ksId, CancellationToken cancellationToken)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null) return null;

        var rows = await _jobs.ListAsync(ksId, cancellationToken).ConfigureAwait(false);
        var items = rows.Select(ProjectToOut).ToList();
        return new { items, total = items.Count };
    }

    /// <summary>
    /// Open a fresh pending job row + kick off the background
    /// <see cref="ExportRunner"/>. Returns the wire DTO so the dispatcher
    /// can hand it to the controller.
    /// </summary>
    public async Task<ExportOut?> CreateAsync(
        Guid ksId, ExportRequest body, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null) return null;

        // Validate layer + shard-size bounds. Mirrors the Python pydantic
        // `ExportRequest` (range 1_000..5_000_000, enum layer).
        if (!ExportLayer.IsValid(body.Layer))
        {
            throw new ValidationException($"Unsupported export layer: {body.Layer}.");
        }
        if (body.ShardSize < 1_000 || body.ShardSize > 5_000_000)
        {
            throw new ValidationException(
                "Shard size must be between 1_000 and 5_000_000.");
        }

        // Release-bound exports are not implemented in this MVP; Python
        // emits the bundle from the serving store, .NET doesn't have a
        // per-release serving snapshot yet (slice 8). Refuse early so
        // the caller can tell the difference between "rejected" and
        // "will run on workspace".
        if (body.ReleaseId is { } relId)
        {
            throw new ValidationException(
                "Release-bound exports are not implemented in this release.");
        }

        // Insert pending row BEFORE Task.Run so the dispatcher's
        // RunWithExtractionGuardAsync rejection check sees the row's
        // "pending" status. If we kicked off the runner first, the guard
        // would race the insert and a concurrent create_export could
        // see no rows and pass through, producing a stranded job.
        var actorId = Guid.TryParse(actor.UserId, out var aid) ? aid : (Guid?)null;
        var actorName = string.IsNullOrEmpty(actor.DisplayName)
            ? "system" : actor.DisplayName!;
        var job = await _jobs.CreateAsync(
            ksId,
            releaseId: null,
            layer: body.Layer,
            shardSize: body.ShardSize,
            format: "nquads",
            createdById: actorId,
            createdByName: actorName,
            cancellationToken).ConfigureAwait(false);

        // Mirror ExtractionOrchestrator.cs:195-208: the chat capacity
        // coordinator's AsyncLocal re-entry tracking must not leak from
        // the HTTP caller into the background worker. The runner here
        // doesn't share chat capacity, but the same principle applies
        // for any future scoped state we add.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                () => _runner.RunAsync(job, ks, CancellationToken.None),
                CancellationToken.None);
        }

        return ProjectToOut(job);
    }

    /// <summary>
    /// Fetch a single job by either Guid or legacy long id. Returns
    /// <c>null</c> when the row is missing so the dispatcher can
    /// degrade to the empty-job placeholder.
    /// </summary>
    public async Task<ExportOut?> GetAsync(
        Guid ksId, string? jobResourceId, CancellationToken cancellationToken)
    {
        var row = await _jobs.ResolveAsync(_db, ksId, jobResourceId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ProjectToOut(row);
    }

    /// <summary>
    /// Read a previously-exported shard from disk and surface it as a
    /// raw response. Throws <see cref="ExportFilePayloadException"/> so
    /// <see cref="FastApiErrorMiddleware"/> can write the bytes + headers
    /// without the JSON envelope. Returns a placeholder DTO shape for
    /// the dispatcher's typing convenience (the caller never sees it).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// When the job is missing, not in <c>completed</c> state, the
    /// filename fails the path-traversal guard, or the filename isn't in
    /// the row's <c>Files</c> JSON. Surfaces as 404.
    /// </exception>
    public async Task<object?> DownloadFileAsync(
        Guid ksId, string? jobResourceId, string filename, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, cancellationToken)
            .ConfigureAwait(false);
        var row = await _jobs.ResolveAsync(_db, ksId, jobResourceId, cancellationToken)
            .ConfigureAwait(false);

        if (ks is null || row is null || row.Status != "completed")
        {
            throw new KeyNotFoundException("Export file not found.");
        }

        // Confirm the filename is part of this job's published file list
        // (defends against guessing sibling job artefacts by id collision).
        var fileNames = row.Files is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : row.Files.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var n)
                    ? n.GetString() ?? string.Empty
                    : string.Empty)
                .ToHashSet(StringComparer.Ordinal);
        if (!fileNames.Contains(filename))
        {
            throw new KeyNotFoundException("Export file not found.");
        }

        var bytes = _artifacts.ReadFile(ks.PublicId, row.LegacyId, filename);
        if (bytes is null)
        {
            throw new KeyNotFoundException("Export file not found.");
        }

        // Media-type selection mirrors the Python _download_export_file
        // mapping on releases.py:759-771: .nq → application/n-quads;
        // .jsonl → application/x-ndjson; everything else → application/json.
        var mediaType = filename.EndsWith(".nq", StringComparison.OrdinalIgnoreCase)
            ? "application/n-quads"
            : filename.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? "application/x-ndjson"
                : "application/json";

        // Throw the sentinel — middleware catches it and writes the raw
        // response. The return value below is unreachable in practice
        // (the throw propagates), but the dispatcher arm needs a
        // non-null Task<object?> return for its WrapAsync signature.
        throw new ExportFilePayloadException(bytes, mediaType, filename);
    }

    /// <summary>
    /// Snake-case JSON options matching the policy
    /// <see cref="ExportJobStore.RecordFilesAsync"/> uses when
    /// persisting the descriptor list.
    /// </summary>
    private static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
    }

    /// <summary>
    /// Project a job row to the wire DTO. Deserialises <c>Files</c>
    /// (JSON column) back into the typed descriptor list so the
    /// <c>SnakeCaseLower</c> policy hands a clean array to the frontend.
    /// </summary>
    private static ExportOut ProjectToOut(ExportJobEntity row)
    {
        IReadOnlyList<ExportFileEntry> files;
        if (row.Files is null)
        {
            files = Array.Empty<ExportFileEntry>();
        }
        else
        {
            // Match the snake-case policy ExportJobStore.RecordFilesAsync
            // uses when serialising the descriptor list into the Files
            // JSON column.
            files = JsonSerializer.Deserialize<List<ExportFileEntry>>(
                row.Files.RootElement.GetRawText(), WireJson.Options) ?? new();
        }
        return new ExportOut(
            Id: row.Id,
            KnowledgeSystemId: row.KnowledgeSystemId,
            ReleaseId: row.ReleaseId,
            Layer: row.Layer,
            Format: row.Format,
            Status: row.Status,
            ShardSize: row.ShardSize,
            ProcessedStatements: row.ProcessedStatements,
            TotalStatements: row.TotalStatements,
            Files: files,
            Error: row.Error,
            CreatedBy: row.CreatedByName,
            CreatedAt: row.CreatedAt,
            StartedAt: row.StartedAt,
            FinishedAt: row.FinishedAt);
    }
}