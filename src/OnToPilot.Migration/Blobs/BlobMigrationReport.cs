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
/// Skip accounting is split into two independent counters so an operator
/// reading the report can tell at a glance which blobs were left out of
/// the manifest and why:
/// </para>
/// <list type="bullet">
///   <item><see cref="ResumeSkippedCount"/> — blobs the state store
///   remembered from a prior run. They ARE recorded in
///   <see cref="Entries"/> (with their reference count and SHA) so the
///   manifest still lists every document-referenced blob, but they
///   were NOT re-uploaded or re-verified.</item>
///   <item><see cref="ZeroReferenceSkippedCount"/> — blobs no
///   <c>document.storagepath</c> row references. They are NOT
///   recorded in <see cref="Entries"/>; the migration treats them as
///   orphan / release artifacts and never touches MinIO.</item>
/// </list>
/// <para>With these two fields, <see cref="Entries"/>.Count always
/// equals <see cref="UploadedCount"/> + <see cref="ResumeSkippedCount"/>.</para>
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

    /// <summary>
    /// Number of blobs already in the state store at the start of the
    /// run. These blobs ARE present in <see cref="Entries"/> (the
    /// manifest records every document-referenced blob regardless of
    /// whether it was uploaded fresh) but were NOT re-uploaded or
    /// re-verified.
    /// </summary>
    public int ResumeSkippedCount { get; }

    /// <summary>
    /// Number of blobs whose <c>document.storagepath</c> reference count
    /// was zero. These blobs are NOT in <see cref="Entries"/> and were
    /// NOT uploaded — they are orphan / release artifacts.
    /// </summary>
    public int ZeroReferenceSkippedCount { get; }

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
        int resumeSkippedCount,
        int zeroReferenceSkippedCount,
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
        ResumeSkippedCount = resumeSkippedCount;
        ZeroReferenceSkippedCount = zeroReferenceSkippedCount;
        CorruptedCount = corruptedCount;
        FinishedAtUtc = finishedAtUtc;
    }
}
