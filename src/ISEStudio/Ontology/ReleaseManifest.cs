namespace ISEStudio.Ontology;

// ---------------------------------------------------------------------------
// Release manifest records.
// ---------------------------------------------------------------------------

/// <summary>
/// Per-shard description of a single layer within a release. The signature is
/// the SHA-256 of the N-Quads bytes on disk; statement count is the count at
/// the time the shard was written (so a corrupted shard with the right hash
/// but wrong row count is detectable by clients).
/// </summary>
public sealed record ReleaseFileManifest(
    string Layer,
    string FileName,
    long StatementCount,
    string Sha256);

/// <summary>
/// Top-level manifest persisted as <c>manifest.json</c> inside every release
/// directory. <see cref="Version"/> is the public version string (<c>v1</c>,
/// <c>v2</c>, …) assigned at capture time; <see cref="ProvenanceCount"/> is
/// the rolled-up count across the three layers (used by the immutable
/// serving views for downstream pagination hints).
/// </summary>
public sealed record ReleaseManifest(
    string Version,
    IReadOnlyList<ReleaseFileManifest> Files,
    long ProvenanceCount);

/// <summary>
/// One captured / published release. <see cref="Id"/> is the internal draft
/// id (a Guid) used by the artifact directory; <see cref="Version"/> is the
/// human-visible version string. <see cref="Path"/> is the absolute filesystem
/// path of the physically separate RocksDB that serves the published view —
/// it is also where the serving <see cref="StoreWrapper"/> is rooted at
/// publication time.
/// </summary>
public sealed record Release(
    string Id,
    string Version,
    KsContext Ks,
    string Path);