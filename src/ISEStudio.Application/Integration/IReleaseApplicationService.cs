using ISEStudio.Application.Foundation;
using ISEStudio.Application.Releases;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application service for the sixteen <c>releases.*</c> operations the
/// internal REST contract exposes. Twelve go through <c>ReleaseService</c>
/// (lifecycle: <c>list</c>, <c>create</c>, <c>review</c>, <c>publish</c>,
/// <c>deploy</c>, <c>stop_deployment</c>, <c>delete</c>, <c>rollback</c>,
/// <c>diff</c> + the four exports: <c>list_exports</c>,
/// <c>create_export</c>, <c>get_export</c>,
/// <c>download_export_file</c>).
/// <para>
/// Each method unpacks one <see cref="InternalRequest"/> (path / query /
/// body / actor), delegates to the underlying domain service, and returns
/// the strongly-typed DTO the dispatcher serialises &mdash; or
/// <c>null</c> when the operation has no body / no resource id.
/// </para>
/// <para>
/// <b>Special return shapes.</b>
/// <list type="bullet">
/// <item><c>CreateDraftAsync</c>: body fields (<c>title</c>, <c>notes</c>)
/// are optional &mdash; empty strings degrade to the Python defaults.
/// The service itself returns the <see cref="ReleaseOut"/>; the
/// dispatcher projects it to the snake_case wire shape.</item>
/// <item><c>RollbackAsync</c>: returns the anonymous <c>{restored, version}</c>
/// envelope from <c>ReleaseService.RollbackAsync</c>. We expose it as
/// <see cref="ReleaseRollbackResponse"/> to keep the typed surface
/// stable; the dispatcher passes the shape through verbatim.</item>
/// <item><c>DiffAsync</c>: returns the anonymous <c>{from, to, layers}</c>
/// envelope from <c>ReleaseService.DiffAsync</c>. Exposed as <see cref="object"/>
/// since the layers dictionary is a free-form shape; the dispatcher
/// passes it through verbatim.</item>
/// <item><c>DownloadExportFileAsync</c>: throws <c>ExportFilePayloadException</c>
/// which <c>FastApiErrorMiddleware</c> catches to write raw bytes + headers
/// (no JSON envelope). The service returns a placeholder bytes array &mdash;
/// the dispatcher doesn't serialise it.</item>
/// </list>
/// </para>
/// </summary>
public interface IReleaseApplicationService
{
    // ----------------------------------------------------------------------
    // Release lifecycle (ReleaseService) — 12 op
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>releases.list</c> &mdash; every release row for the KS,
    /// newest first. Returns <c>null</c> when the KS id is missing;
    /// dispatcher maps to <c>{items:[], total:0}</c>.
    /// </summary>
    Task<object?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.create</c> &mdash; open a draft row + synchronously
    /// capture the snapshot. The body fields <c>title</c> / <c>notes</c>
    /// are read from the loose <c>"_"</c> envelope; both default to
    /// <see cref="string.Empty"/> matching the Python defaults.
    /// </summary>
    Task<ReleaseOut?> CreateDraftAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.review</c> &mdash; run the quality gate, mark the
    /// row <c>reviewed</c>. Body carries an optional <c>note</c>.
    /// Returns <c>null</c> when the KS id or resource id is missing or
    /// doesn't parse; dispatcher maps to the empty <see cref="ReleaseOut"/>.
    /// </summary>
    Task<ReleaseOut?> ReviewAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.publish</c> &mdash; assign <c>v{N}</c> + materialise
    /// the per-release serving store. Body carries an optional <c>note</c>.
    /// </summary>
    Task<ReleaseOut?> PublishAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.deploy</c> &mdash; activate the deployment row +
    /// re-materialise the serving store.
    /// </summary>
    Task<ReleaseOut?> DeployAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.stop_deployment</c> &mdash; flip the deployment row
    /// to <c>stopped</c> + close the serving store.
    /// </summary>
    Task<ReleaseOut?> StopDeploymentAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.delete</c> &mdash; flip the row to <c>deleted</c> +
    /// close the serving store + drop artefacts.
    /// </summary>
    Task<ReleaseOut?> DeleteAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.rollback</c> &mdash; restore the three workspace
    /// graphs from the immutable snapshot + clear governance queues.
    /// Returns the <see cref="ReleaseRollbackResponse"/>
    /// (<c>{restored, version}</c>) on success or <c>null</c> when the
    /// KS id or resource id is missing.
    /// </summary>
    Task<ReleaseRollbackResponse?> RollbackAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.diff</c> &mdash; per-layer semantic set-diff between
    /// two release snapshots. Query carries <c>from_id</c> + <c>to_id</c>
    /// (both <see cref="Guid"/>). Returns the anonymous envelope
    /// (<c>{from, to, layers}</c>) on success or <c>null</c> when any
    /// parameter is missing or unparseable.
    /// </summary>
    Task<object?> DiffAsync(InternalRequest request, CancellationToken cancellationToken);

    // ----------------------------------------------------------------------
    // Release exports (ExportService) — 4 op
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>releases.list_exports</c> &mdash; every export job for the KS,
    /// newest first. Returns <c>null</c> when the KS id is missing;
    /// dispatcher maps to <c>{items:[], total:0}</c>.
    /// </summary>
    Task<object?> ListExportsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.create_export</c> &mdash; open a pending job row +
    /// kick off the background runner. Body carries <c>layer</c>
    /// (default <c>bundle</c>), <c>release_id</c> (optional), and
    /// <c>shard_size</c> (default 100_000).
    /// </summary>
    Task<ExportOut?> CreateExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.get_export</c> &mdash; one export job by id.
    /// </summary>
    Task<ExportOut?> GetExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>releases.download_export_file</c> &mdash; reads a previously
    /// exported shard from disk and throws <c>ExportFilePayloadException</c>
    /// which the middleware catches to write a raw-bytes response with
    /// <c>Content-Type</c> + <c>Content-Disposition</c>. The
    /// <c>SecondResourceId</c> carries the filename.
    /// </summary>
    Task DownloadExportFileAsync(InternalRequest request, CancellationToken cancellationToken);
}