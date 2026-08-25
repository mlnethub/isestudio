using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ISEStudio.Observability;

/// <summary>
/// Central registration point for every <see cref="ActivitySource"/> and
/// <see cref="Meter"/> the .NET backend owns. The OpenTelemetry builder in
/// <c>Program.cs</c> subscribes to <see cref="AllSourceNames"/> (via the
/// <c>"ISEStudio.*"</c> wildcard) and to <see cref="MeterName"/> so a new
/// source/meter only needs to be added here — no <c>Program.cs</c> edit.
///
/// <para>Source naming follows the brief: every boundary emits an
/// <c>ISEStudio.&lt;layer&gt;</c> activity, with the layer matching the
/// existing services that wrap the call:</para>
/// <list type="bullet">
///   <item><see cref="LlmSourceName"/> — chat / embedding calls.</item>
///   <item><see cref="RdfSourceName"/> — <c>StoreWrapper</c> + SHACL.</item>
///   <item><see cref="ParsingSourceName"/> — <c>DocumentParser</c>.</item>
///   <item><see cref="StorageSourceName"/> — MinIO blob reads / writes.</item>
///   <item><see cref="McpSourceName"/> — MCP tool invocations.</item>
/// </list>
///
/// <para>The brief mandates that logs never contain API keys, bearer
/// tokens, session tokens, or document bodies — see
/// <see cref="SecretRedactionProcessor"/> for the Serilog-side guard.
/// Activity tags follow the same rule: callers use the
/// <see cref="TelemetryExtensions.WithLlmActivity"/> /
/// <see cref="TelemetryExtensions.WithRdfActivity"/> / ... helpers to
/// stamp a <c>llm.provider</c> tag, but never the key, prompt, or
/// response text.</para>
/// </summary>
public static class Telemetry
{
    /// <summary>Source name for LLM chat / embedding activities.</summary>
    public const string LlmSourceName = "ISEStudio.Llm";

    /// <summary>Source name for RDF store + SHACL activities.</summary>
    public const string RdfSourceName = "ISEStudio.Rdf";

    /// <summary>Source name for document parsing activities.</summary>
    public const string ParsingSourceName = "ISEStudio.Parsing";

    /// <summary>Source name for blob storage activities.</summary>
    public const string StorageSourceName = "ISEStudio.Storage";

    /// <summary>Source name for MCP tool activities.</summary>
    public const string McpSourceName = "ISEStudio.Mcp";

    /// <summary>Meter name for extraction metrics (counters + histograms).</summary>
    public const string MeterName = "ISEStudio";

    /// <summary>
    /// All owned source names. Used by callers (tests, the SDK / meter
    /// listeners) that need to subscribe to every source without relying on
    /// the OTel <c>"ISEStudio.*"</c> wildcard filter (which only works
    /// when the SDK is configured).
    /// </summary>
    public static IReadOnlyList<string> AllSourceNames { get; } = new[]
    {
        LlmSourceName,
        RdfSourceName,
        ParsingSourceName,
        StorageSourceName,
        McpSourceName,
    };

    /// <summary>LLM activity source. Created lazily on first access.</summary>
    public static ActivitySource LlmSource { get; } = new(LlmSourceName);

    /// <summary>RDF activity source. Created lazily on first access.</summary>
    public static ActivitySource RdfSource { get; } = new(RdfSourceName);

    /// <summary>Parsing activity source. Created lazily on first access.</summary>
    public static ActivitySource ParsingSource { get; } = new(ParsingSourceName);

    /// <summary>Storage activity source. Created lazily on first access.</summary>
    public static ActivitySource StorageSource { get; } = new(StorageSourceName);

    /// <summary>MCP activity source. Created lazily on first access.</summary>
    public static ActivitySource McpSource { get; } = new(McpSourceName);

    /// <summary>
    /// Shared <see cref="Meter"/> for extraction counters / histograms.
    /// The OTel builder subscribes to <see cref="MeterName"/> ("ISEStudio")
    /// so this meter is exported automatically.
    /// </summary>
    public static Meter Meter { get; } = new(MeterName);

    /// <summary>
    /// Counts each extraction chunk the LLM processes, tagged by provider
    /// and result (<c>success</c> / <c>skipped</c> / <c>error</c>).
    /// </summary>
    public static Counter<long> ExtractionsStarted { get; } =
        Meter.CreateCounter<long>(
            name: "isestudio.extraction.started",
            unit: "{chunk}",
            description: "Number of LLM extraction chunk invocations started.");

    /// <summary>
    /// Counts each extraction chunk that finished, tagged by provider and
    /// outcome (<c>success</c> / <c>skipped</c> / <c>error</c>).
    /// </summary>
    public static Counter<long> ExtractionsCompleted { get; } =
        Meter.CreateCounter<long>(
            name: "isestudio.extraction.completed",
            unit: "{chunk}",
            description: "Number of LLM extraction chunk invocations completed.");

    /// <summary>
    /// Records the wall-clock duration of each LLM extraction chunk in
    /// milliseconds, tagged by provider and outcome. Histogram buckets
    /// cover the brief's expected range (10 ms – 30 s); production scrapers
    /// can pick the buckets they need.
    /// </summary>
    public static Histogram<double> ExtractionDuration { get; } =
        Meter.CreateHistogram<double>(
            name: "isestudio.extraction.duration",
            unit: "ms",
            description: "Wall-clock duration of an LLM extraction chunk in milliseconds.");
}