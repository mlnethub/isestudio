namespace OnToPilot.Migration.Blobs;

/// <summary>
/// Per-blob state record the command persists after each successful
/// upload. The brief mandates this shape: <c>{ sha256 -&gt; { uploadedUtc,
/// size, verified } }</c>.
/// </summary>
/// <param name="UploadedUtc">UTC timestamp the upload completed.</param>
/// <param name="Size">Byte length of the blob that was uploaded.</param>
/// <param name="Verified">
/// <see langword="true"/> when the command successfully re-fetched the
/// object from MinIO and re-hashed it to confirm the round-trip; this is
/// the load-bearing corruption-detection step.
/// </param>
public sealed record BlobMigrationState(DateTimeOffset UploadedUtc, long Size, bool Verified);

/// <summary>
/// Persistence boundary for the blob migration resume log. Implementations
/// must be safe to call from concurrent command runs (the file-based
/// implementation serialises via a process-wide lock).
/// </summary>
/// <remarks>
/// The default JSON-file implementation
/// (<see cref="JsonBlobMigrationStateStore"/>) writes one entry per
/// successfully-uploaded blob under <c>.artifacts/blob-state.json</c>.
/// On resume <see cref="BlobMigrationCommand"/> calls
/// <see cref="GetCompletedAsync"/> and skips any blob whose SHA-256 is
/// already in the log.
/// </remarks>
public interface IBlobMigrationStateStore
{
    /// <summary>
    /// Snapshot the current set of completed SHAs. Used at the top of
    /// every <c>RunAsync</c> invocation to decide which blobs to skip.
    /// </summary>
    Task<IReadOnlyDictionary<string, BlobMigrationState>> GetCompletedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Append <paramref name="state"/> to the log under
    /// <paramref name="sha256"/>. Called immediately after a successful
    /// upload + verify round-trip.
    /// </summary>
    Task MarkCompletedAsync(string sha256, BlobMigrationState state, CancellationToken cancellationToken);
}
