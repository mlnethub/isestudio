namespace OnToPilot.Migration.Blobs;

/// <summary>
/// One row in the blob migration manifest. The shape is dictated by
/// <c>migration/manifests/blob-manifest.schema.json</c> (draft 2020-12)
/// and is the contract Task 4's <c>Assert-AllMigrationManifests</c>
/// gate validates against.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SourcePath"/> is the POSIX-style path relative to the
/// Python blob root (e.g. <c>ab/cd/abcdef01...</c>). <see cref="ObjectKey"/>
/// is what landed in MinIO. The two values are identical in the current
/// implementation because <see cref="OnToPilot.Storage.BlobKey.LegacyPathFor"/>
/// produces the same 3-segment fanout the Python backend used; keeping
/// them as separate fields preserves the option of changing the MinIO
/// layout (e.g. flat <c>{sha}</c>) without rewriting the manifest
/// schema.
/// </para>
/// <para>
/// <see cref="ReferenceCount"/> is the number of <c>document.storagepath</c>
/// rows pointing at this blob in PostgreSQL. The migration skips any blob
/// with a count of zero (orphan / release artifact), so every manifest
/// entry has <c>ReferenceCount &gt;= 1</c>.
/// </para>
/// </remarks>
/// <param name="SourcePath">POSIX-style legacy layout path (e.g. <c>ab/cd/abcdef01...</c>).</param>
/// <param name="ObjectKey">Object key written to (or planned for) the MinIO bucket.</param>
/// <param name="Size">Byte length of the blob.</param>
/// <param name="Sha256">Lowercase-hex SHA-256 digest of the blob bytes.</param>
/// <param name="ReferenceCount">Number of <c>document.storagepath</c> rows referencing this blob.</param>
public sealed record BlobManifestEntry(
    string SourcePath,
    string ObjectKey,
    long Size,
    string Sha256,
    int ReferenceCount);
