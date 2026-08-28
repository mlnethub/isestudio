namespace ISEStudio.Application.Releases;

/// <summary>
/// Canonical layer strings the Python <c>backend/app/api/releases.py</c>
/// <c>_export_out()</c> shape exposes via <c>layer</c>. The wire values
/// are pinned lower-case; <see cref="Expand"/> turns a <c>bundle</c>
/// layer into the per-physical-layer list the runner iterates over.
/// </summary>
public static class ExportLayer
{
    public const string TBox = "tbox";
    public const string Vocabulary = "vocabulary";
    public const string ABox = "abox";
    public const string Bundle = "bundle";

    public static readonly string[] All = { TBox, Vocabulary, ABox, Bundle };

    public static bool IsValid(string? layer) =>
        !string.IsNullOrEmpty(layer) && Array.IndexOf(All, layer) >= 0;

    /// <summary>
    /// Per-physical-layer list the runner writes shards for. <c>bundle</c>
    /// expands to all three; the others stay single-element so the runner
    /// only dumps the requested layer.
    /// </summary>
    public static IReadOnlyList<string> Expand(string layer) =>
        layer == Bundle
            ? new[] { TBox, Vocabulary, ABox }
            : new[] { layer };
}

/// <summary>
/// Per-shard manifest row. Mirrors the inner objects the Python
/// <c>_run_export()</c> writes into <c>manifest.json</c> so the frontend
/// can render the file picker (name + size + sha).
/// </summary>
public sealed record ExportFileEntry(
    string Name,
    string Layer,
    long Statements,
    long Bytes,
    string Sha256);

/// <summary>
/// Loose-body input to <c>POST /api/knowledge/{id}/exports</c>. Mirrors
/// the Python <c>ExportRequest</c> pydantic model
/// (<c>backend/app/api/releases.py</c>:716): <c>layer</c> is required,
/// <c>release_id</c> optional, <c>shard_size</c> optional (default
/// 100_000, range 1_000 .. 5_000_000).
/// </summary>
public sealed record ExportRequest(
    string Layer = ExportLayer.Bundle,
    Guid? ReleaseId = null,
    int ShardSize = 100_000);

/// <summary>
/// Wire DTO matching the Python <c>_export_out()</c> shape
/// (<c>backend/app/api/releases.py</c>:95-112). Snake-case via the global
/// <c>JsonNamingPolicy.SnakeCaseLower</c> configured in <c>Program.cs</c>.
/// </summary>
public sealed record ExportOut(
    Guid Id,
    Guid KnowledgeSystemId,
    Guid? ReleaseId,
    string Layer,
    string Format,
    string Status,
    int ShardSize,
    int ProcessedStatements,
    int TotalStatements,
    IReadOnlyList<ExportFileEntry> Files,
    string? Error,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);