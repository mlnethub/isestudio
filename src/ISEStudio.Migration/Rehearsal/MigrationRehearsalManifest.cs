using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Migration.Rehearsal;

[JsonConverter(typeof(MigrationStepStatusJsonConverter))]
public enum MigrationStepStatus
{
    Started,
    Passed,
    Failed,
    Skipped,
}

public sealed record MigrationRehearsalManifest(
    string RunId,
    string DatabaseMode,
    IReadOnlyList<MigrationStepResult> Steps,
    bool Passed,
    DateTimeOffset CompletedAt,
    string? ManifestChecksum = null);

public sealed record MigrationStepResult(
    string Name,
    MigrationStepStatus Status,
    long Rows,
    string Checksum,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MigrationRehearsalOptions(
    string DatabaseMode,
    string? PostgresConnectionString,
    string ManifestPath,
    string? BackupPath = null,
    string? RdfSourcePath = null,
    string? RdfCopyPath = null,
    string? RdfWorkPath = null,
    string? BlobManifestPath = null,
    string? ExpectedBackupSha256 = null,
    long? ExpectedBackupSize = null)
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

public sealed class MigrationStepStatusJsonConverter : JsonConverter<MigrationStepStatus>
{
    public override MigrationStepStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToLowerInvariant() switch
        {
            "started" => MigrationStepStatus.Started,
            "passed" => MigrationStepStatus.Passed,
            "failed" => MigrationStepStatus.Failed,
            "skipped" => MigrationStepStatus.Skipped,
            _ => throw new JsonException("Unknown migration step status."),
        };

    public override void Write(Utf8JsonWriter writer, MigrationStepStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}