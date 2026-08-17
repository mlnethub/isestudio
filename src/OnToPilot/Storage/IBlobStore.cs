namespace OnToPilot.Storage;

/// <summary>
/// Result of storing a blob: the lowercase-hex SHA-256 that the store
/// computed (or matched, on an idempotent re-write) plus the legacy
/// 3-segment storage path that downstream <c>Document.storage_path</c>
/// rows reference for traceability across the migration.
/// </summary>
/// <param name="Sha256">Lowercase-hex SHA-256 of the written bytes.</param>
/// <param name="LegacyStoragePath">Legacy layout <c>{aa}/{bb}/{full_sha}</c>.</param>
public sealed record BlobWriteResult(string Sha256, string LegacyStoragePath);

/// <summary>
/// Content-addressed blob storage. Implementations guarantee that two
/// writes with the same bytes produce the same <see cref="BlobWriteResult"/>,
/// and that re-writing an existing identical blob is a no-op (idempotent).
/// </summary>
/// <remarks>
/// <para>
/// Implementations <strong>do not</strong> track reference counts. A call to
/// <see cref="RemoveAsync"/> deletes the blob unconditionally; the caller is
/// responsible for ensuring no <c>Document</c> still references the SHA before
/// invoking it. Reference counting is left to the extraction/orchestration
/// pipeline that Task 4 will wire up.
/// </para>
/// <para>
/// The supplied <see cref="Stream"/> is read to completion and not disposed
/// by the implementation; callers retain ownership.
/// </para>
/// </remarks>
public interface IBlobStore
{
    /// <summary>
    /// Stream <paramref name="content"/> into the store, hashing it as it
    /// arrives. If an object with the same SHA-256 already exists, the
    /// existing one is returned and no second write occurs.
    /// </summary>
    Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Open the blob for reading. Returns <see langword="null"/> when no blob
    /// with the supplied SHA exists; otherwise returns a stream the caller
    /// owns (must be disposed).
    /// </summary>
    Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken);

    /// <summary>Whether a blob with the supplied SHA currently exists.</summary>
    Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken);

    /// <summary>
    /// Delete the blob. Returns <see langword="true"/> when a blob was
    /// removed, <see langword="false"/> when nothing matched.
    /// </summary>
    Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken);
}
