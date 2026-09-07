using System.Text.Json.Serialization;

namespace ISEStudio.Migration.Rehearsal;

public sealed record MigrationRehearsalManifest(
    string RunId,
    string DatabaseMode,
    IReadOnlyList<MigrationStepResult> Steps,
    bool Passed,
    DateTimeOffset CompletedAt);

public sealed record MigrationStepResult(
    string Name,
    string Status,
    long Rows,
    string Checksum,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record MigrationRehearsalOptions(
    string DatabaseMode,
    string? PostgresConnectionString,
    string ManifestPath,
    string? BackupPath = null,
    string? RdfSourcePath = null,
    string? RdfCopyPath = null,
    string? RdfWorkPath = null,
    string? BlobManifestPath = null)
{
    public static readonly IReadOnlyList<string> RequiredStepNames =
    [
        "database-connectivity",
        "ef-migrations",
        "schema-assertions",
        "sql-snapshot",
        "rdf-copy",
        "blob-manifest",
        "graph-read-only",
        "report-serialization",
    ];
}