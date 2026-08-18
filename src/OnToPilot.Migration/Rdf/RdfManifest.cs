using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnToPilot.Migration.Rdf;

/// <summary>
/// Serializable record of a single RDF migration run. Emitted by
/// <see cref="RdfMigrationCommand.VerifyCopyAsync"/> as JSON so the
/// rehearsal / cutover orchestration (Task 4) can diff the direct-read
/// and N-Quads-fallback manifests and detect any drift between the
/// strategies.
///
/// <para>The shape mirrors the brief's <c>RdfMigrationReport</c> plus a
/// few Task-2-only audit fields:
/// <list type="bullet">
///   <item><c>Strategy</c> — <c>"direct"</c> or <c>"nquads"</c>.</item>
///   <item><c>QuadCount</c> — total quads observed on the chosen strategy.</item>
///   <item><c>NamedGraphs</c> — distinct named graphs, sorted.</item>
///   <item><c>QueryResultHashes</c> — per-query SHA-256 over the JSON
///   serialised result set (deterministic; Oxigraph's
///   <c>QuerySolutions.Serialize</c> with the JSON format).</item>
///   <item><c>WriteRevertPassed</c> — populated by
///   <see cref="RdfMigrationCommand.WriteRevertSmokeAsync"/>.</item>
///   <item><c>SourceOpenedByDotNet</c> — structural guarantee that the
///   command never instantiated an <c>OxigraphStore</c> with the source
///   path. Stays <c>false</c> by construction.</item>
/// </list>
/// </para>
/// </summary>
public sealed record RdfManifest
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = string.Empty;

    [JsonPropertyName("quadCount")]
    public ulong QuadCount { get; init; }

    [JsonPropertyName("namedGraphs")]
    public IReadOnlyList<string> NamedGraphs { get; init; } = Array.Empty<string>();

    [JsonPropertyName("queryResultHashes")]
    public IReadOnlyDictionary<string, string> QueryResultHashes { get; init; }
        = new Dictionary<string, string>();

    [JsonPropertyName("writeRevertPassed")]
    public bool WriteRevertPassed { get; init; }

    /// <summary>
    /// Hard structural flag — the command never opens the source path.
    /// Stays <c>false</c> by construction; exposed so the verification
    /// test can assert it explicitly.
    /// </summary>
    [JsonPropertyName("sourceOpenedByDotNet")]
    public bool SourceOpenedByDotNet { get; init; }

    /// <summary>Path of the copy directory the manifest is over.</summary>
    [JsonPropertyName("copyPath")]
    public string CopyPath { get; init; } = string.Empty;

    /// <summary>Path of the work directory used for the N-Quads fallback.</summary>
    [JsonPropertyName("workPath")]
    public string WorkPath { get; init; } = string.Empty;

    /// <summary>Wall-clock time the run completed at (UTC).</summary>
    [JsonPropertyName("finishedAtUtc")]
    public DateTimeOffset FinishedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Serialise to a JSON string (Task 4 expects UTF-8 JSON).</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, ManifestJson.Options);
    }

    /// <summary>Deserialise from JSON; used by the parity script when it
    /// diffs the direct and fallback manifests.</summary>
    public static RdfManifest FromJson(string json)
    {
        return JsonSerializer.Deserialize<RdfManifest>(json, ManifestJson.Options)
            ?? throw new InvalidDataException("RdfManifest JSON deserialised to null.");
    }

    private static class ManifestJson
    {
        internal static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }
}
