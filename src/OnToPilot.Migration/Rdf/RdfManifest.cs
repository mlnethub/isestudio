using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnToPilot.Migration.Rdf;

/// <summary>
/// The five-field positional record the brief mandates for the RDF
/// migration report. This is the data-only, Task-4-orchestrator-facing
/// shape — audit / provenance fields live on the sibling
/// <see cref="RdfMigrationAudit"/> record so the brief's "exactly five
/// fields" constraint is honoured verbatim.
///
/// <para>Field semantics:
/// <list type="bullet">
///   <item><c>Strategy</c> — <c>"direct"</c> (OxigraphStore.OpenReadOnly
///   on the copy) or <c>"nquads"</c> (Load(N-Quads) on a fresh store at
///   <c>workPath</c>).</item>
///   <item><c>QuadCount</c> — total quads observed on the chosen
///   strategy.</item>
///   <item><c>NamedGraphs</c> — distinct named graphs, sorted.</item>
///   <item><c>QueryResultHashes</c> — per-query SHA-256 over the JSON
///   serialised result set (deterministic; Oxigraph's
///   <c>QuerySolutions.Serialize</c> with the JSON format).</item>
///   <item><c>WriteRevertPassed</c> — populated by
///   <see cref="RdfMigrationCommand.WriteRevertSmokeAsync"/> via
///   <see cref="WithWriteRevertPassed"/>; the underlying
///   <see cref="RdfMigrationCommand"/> returns a fresh report rather
///   than mutating this one so the record stays a positional record.</item>
/// </list>
/// </para>
/// </summary>
public sealed record RdfMigrationReport(
    string Strategy,
    ulong QuadCount,
    IReadOnlyList<string> NamedGraphs,
    IReadOnlyDictionary<string, string> QueryResultHashes,
    bool WriteRevertPassed)
{
    /// <summary>
    /// Returns a new <see cref="RdfMigrationReport"/> with
    /// <c>WriteRevertPassed</c> flipped to <paramref name="passed"/>. The
    /// rest of the fields are copied by value. Implemented as a `with`
    /// expression so the positional record stays immutable.
    /// </summary>
    public RdfMigrationReport WithWriteRevertPassed(bool passed) =>
        this with { WriteRevertPassed = passed };

    /// <summary>Serialise to JSON for the Task 4 cross-migration manifest.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, RdfManifestJson.Options);

    /// <summary>Deserialise from JSON; used by the parity script when it
    /// diffs the direct and fallback reports.</summary>
    public static RdfMigrationReport FromJson(string json) =>
        JsonSerializer.Deserialize<RdfMigrationReport>(json, RdfManifestJson.Options)
            ?? throw new InvalidDataException("RdfMigrationReport JSON deserialised to null.");
}

/// <summary>
/// Sibling audit record to <see cref="RdfMigrationReport"/>. Carries
/// everything Task 4's orchestrator needs to prove the cutover is safe
/// without polluting the brief's five-field positional record.
///
/// <para>Two design choices the reviewer flagged:
/// <list type="bullet">
///   <item><c>SourceOpenedByDotNet</c> stays <c>false</c> by
///   construction — the production code never instantiates an
///   <c>OxigraphStore</c> with the source path. The verbatim test in
///   <see cref="RdfMigrationCommand"/>'s contract asserts this.</item>
///   <item><c>CleanupSucceeded</c> is set to <c>false</c> by
///   <see cref="RdfMigrationCommand.WriteRevertSmokeAsync"/>'s finally
///   block when the <c>ClearGraph</c> best-effort cleanup itself throws
///   (RocksDB write conflict, IO error). The orchestrator treats
///   <c>!CleanupSucceeded</c> as a hard gate failure.</item>
/// </list>
/// </para>
/// </summary>
public sealed record RdfMigrationAudit(
    bool SourceOpenedByDotNet,
    string CopyPath,
    string WorkPath,
    DateTimeOffset FinishedAtUtc,
    bool CleanupSucceeded,
    string? DirectStrategyError)
{
    /// <summary>JSON serializer options shared with <see cref="RdfMigrationReport"/>.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// JSON serialisation options for <see cref="RdfMigrationReport"/>. Indented
/// for diff-friendliness when the orchestrator compares the direct and
/// fallback reports.
/// </summary>
internal static class RdfManifestJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}