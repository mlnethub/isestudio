namespace OnToPilot.Migration.Blobs;

/// <summary>
/// The in-memory + on-disk result of a single blob migration run.
/// Carries the brief's required shape: a list of <see cref="BlobManifestEntry"/>
/// rows plus the dry-run / resume flags so Task 4's gate can inspect
/// what actually happened without re-parsing the manifest.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Entries"/> is sorted ascending by <c>Sha256</c> so two
/// consecutive runs against an unchanged source tree produce a
/// byte-identical manifest file. This is what makes the manifest a
/// usable artifact for change-detection in CI.
/// </para>
/// <para>
/// <see cref="UploadedCount"/> counts objects that hit MinIO; it does
/// NOT include objects the state store told us to skip. <see cref="SkippedCount"/>
/// counts the state-store skips plus the orphans the command logged a
/// warning about (zero-reference blobs / release artifacts dropped on
/// the floor because they're outside the source tree).
/// </para>
/// </remarks>
public sealed class BlobMigrationReport
{
    /// <summary>Schema version embedded in the manifest; bumped only on shape changes.</summary>
    public const string ManifestVersion = "1.0.0";

    /// <summary>The Python blob root that was walked (absolute path).</summary>
    public string SourceDirectory { get; }

    /// <summary>The MinIO bucket the blobs landed in (or would have, in dry-run).</summary>
    public string Bucket { get; }

    /// <summary>True when the command ran in dry-run mode (no MinIO calls).</summary>
    public bool DryRun { get; }

    /// <summary>True when the command ignored the state store.</summary>
    public bool Force { get; }

    /// <summary>Sortable, deterministic manifest entries (ascending by SHA-256).</summary>
    public IReadOnlyList<BlobManifestEntry> Entries { get; }

    /// <summary>Number of entries the command actually uploaded to MinIO.</summary>
    public int UploadedCount { get; }

    /// <summary>Number of entries skipped (state-store or zero-reference).</summary>
    public int SkippedCount { get; }

    /// <summary>Number of files under <see cref="SourceDirectory"/> that did not match the filename-as-SHA-256 invariant.</summary>
    public int CorruptedCount { get; }

    /// <summary>UTC timestamp at which the run finished writing the manifest (or finished in-memory if no manifest was written).</summary>
    public DateTimeOffset FinishedAtUtc { get; }

    /// <summary>Initialise from the command's run-time state. Sorts <paramref name="entries"/> deterministically.</summary>
    public BlobMigrationReport(
        string sourceDirectory,
        string bucket,
        bool dryRun,
        bool force,
        IReadOnlyList<BlobManifestEntry> entries,
        int uploadedCount,
        int skippedCount,
        int corruptedCount,
        DateTimeOffset finishedAtUtc)
    {
        SourceDirectory = sourceDirectory;
        Bucket = bucket;
        DryRun = dryRun;
        Force = force;
        Entries = entries
            .OrderBy(e => e.Sha256, StringComparer.Ordinal)
            .ToArray();
        UploadedCount = uploadedCount;
        SkippedCount = skippedCount;
        CorruptedCount = corruptedCount;
        FinishedAtUtc = finishedAtUtc;
    }
}
