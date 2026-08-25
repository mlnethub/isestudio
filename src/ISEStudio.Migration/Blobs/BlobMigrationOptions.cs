namespace ISEStudio.Migration.Blobs;

/// <summary>
/// Knobs for <see cref="BlobMigrationCommand.RunAsync"/>. Every field has
/// a sensible default; the record is constructed by both the production
/// code path (driven by <c>Invoke-BlobMigration.ps1</c>) and the
/// integration tests.
/// </summary>
/// <param name="DryRun">
/// When <see langword="true"/> the command does NOT upload anything to
/// MinIO; it still walks the source tree, hashes each blob, looks up
/// <c>ReferenceCount</c> from PostgreSQL, and writes the manifest. Used
/// to preview what a real run would do without touching the bucket.
/// </param>
/// <param name="Force">
/// When <see langword="true"/> the command does not consult the state
/// store — every document-referenced blob is uploaded, even if a previous
/// run already recorded it as complete. Mirrors the
/// <c>--force</c> flag of <c>Invoke-BlobMigration.ps1</c>.
/// </param>
/// <param name="ManifestOut">
/// Absolute path of the manifest JSON to write. When
/// <see langword="null"/> the manifest is NOT written to disk; the
/// <see cref="BlobMigrationReport.Entries"/> collection is still returned
/// in memory. Required for Task 4's
/// <c>Assert-AllMigrationManifests</c> gate to find the file.
/// </param>
/// <param name="StatePath">
/// Absolute path of the JSON state file the command uses to remember
/// which blobs have already been uploaded. When
/// <see langword="null"/> every blob is uploaded (no resume support).
/// See <see cref="IBlobMigrationStateStore"/>.
/// </param>
public sealed record BlobMigrationOptions(
    bool DryRun,
    bool Force,
    string? ManifestOut,
    string? StatePath = null);
