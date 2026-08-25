using System.Diagnostics;
using ISEStudio.Ontology;

namespace ISEStudio.Observability;

/// <summary>
/// Convenience wrappers around <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// for each ISEStudio layer. The helpers centralise three rules the brief
/// and the security review both call out:
///
/// <list type="number">
///   <item>Never stamp secrets (API keys, bearer tokens, prompts, document
///     bodies) onto an activity tag. Only safe identifiers — provider
///     names, model names, durations, error categories — make it onto a
///     tag.</item>
///   <item>Stop the activity in a <c>finally</c> block so an exception
///     escaping the wrapped delegate still records the failure as an
///     <c>error</c> tag and the duration histogram.</item>
///   <item>Use the OTel status <see cref="ActivityStatusCode.Error"/> (not
///     <c>UnsetError</c>) when the delegate throws, so downstream
///     scrapers can alert on the right counter.</item>
/// </list>
/// </summary>
public static class TelemetryExtensions
{
    // Tag keys. Centralised so the search for "where is llm.provider set"
    // lands in one place and the test suite can grep them too.
    internal const string ProviderTag = "llm.provider";
    internal const string ModelTag = "llm.model";
    internal const string PhaseTag = "extraction.phase";
    internal const string OperationTag = "operation.name";
    internal const string PeerServiceTag = "peer.service";
    internal const string GraphTag = "rdf.graph";
    internal const string QuadCountTag = "rdf.quad_count";
    internal const string ViolationCountTag = "shacl.violation_count";
    internal const string ConformsTag = "shacl.conforms";
    internal const string ExtensionTag = "file.extension";
    internal const string BytesTag = "storage.bytes";
    internal const string ToolTag = "mcp.tool";
    internal const string OutcomeTag = "outcome"; // "success" / "skipped" / "error"

    /// <summary>
    /// Run <paramref name="action"/> inside an <see cref="LlmSourceName"/>
    /// activity. Stamps <c>llm.provider</c> / <c>llm.model</c> from
    /// <paramref name="provider"/> / <paramref name="model"/> (never the
    /// API key) and records the duration histogram.
    /// </summary>
    public static async Task<T> WithLlmActivity<T>(
        this ActivitySource source,
        string operationName,
        string provider,
        string model,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity(operationName, ActivityKind.Client);
        activity?.SetTag(PeerServiceTag, provider);
        activity?.SetTag(ProviderTag, provider);
        activity?.SetTag(ModelTag, model);

        var started = Telemetry.ExtractionsStarted;
        var completed = Telemetry.ExtractionsCompleted;
        var duration = Telemetry.ExtractionDuration;
        started.Add(1,
            new KeyValuePair<string, object?>(ProviderTag, provider),
            new KeyValuePair<string, object?>(ModelTag, model));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(OutcomeTag, "success");
            sw.Stop();
            duration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "success"));
            completed.Add(1,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "success"));
            return result;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag(OutcomeTag, "cancelled");
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            sw.Stop();
            duration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "cancelled"));
            completed.Add(1,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            sw.Stop();
            duration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "error"));
            completed.Add(1,
                new KeyValuePair<string, object?>(ProviderTag, provider),
                new KeyValuePair<string, object?>(ModelTag, model),
                new KeyValuePair<string, object?>(OutcomeTag, "error"));
            throw;
        }
    }

    /// <summary>
    /// Run <paramref name="action"/> inside an <see cref="RdfSourceName"/>
    /// activity. Stamps <c>operation.name</c> and optional <c>rdf.graph</c>.
    /// </summary>
    public static async Task<T> WithRdfActivity<T>(
        this ActivitySource source,
        string operationName,
        string? graphIri,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag(PeerServiceTag, "oxigraph");
        activity?.SetTag(OperationTag, operationName);
        if (!string.IsNullOrEmpty(graphIri)) activity?.SetTag(GraphTag, graphIri);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(OutcomeTag, "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Synchronous SHACL validation activity.</summary>
    public static ShaclReportWithDuration WithShaclActivity(
        this ActivitySource source,
        string operationName,
        string graphIri,
        Func<ShaclReport> action)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag(PeerServiceTag, "shacl.validator");
        activity?.SetTag(OperationTag, operationName);
        activity?.SetTag(GraphTag, graphIri);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var report = action();
            sw.Stop();
            activity?.SetTag(ConformsTag, report.Conforms);
            activity?.SetTag(ViolationCountTag, report.Violations.Count);
            activity?.SetTag(OutcomeTag, "success");
            return new ShaclReportWithDuration(report, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Run <paramref name="action"/> inside a <see cref="ParsingSourceName"/> activity.</summary>
    public static async Task<T> WithParsingActivity<T>(
        this ActivitySource source,
        string operationName,
        string extension,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag(PeerServiceTag, "docling");
        activity?.SetTag(OperationTag, operationName);
        if (!string.IsNullOrEmpty(extension)) activity?.SetTag(ExtensionTag, extension);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(OutcomeTag, "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Run <paramref name="action"/> inside a <see cref="StorageSourceName"/> activity.</summary>
    public static async Task<T> WithStorageActivity<T>(
        this ActivitySource source,
        string operationName,
        long? bytes,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity(operationName, ActivityKind.Client);
        activity?.SetTag(PeerServiceTag, "minio");
        activity?.SetTag(OperationTag, operationName);
        if (bytes.HasValue) activity?.SetTag(BytesTag, bytes.Value);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(OutcomeTag, "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Run <paramref name="action"/> inside a <see cref="McpSourceName"/> activity.</summary>
    public static async Task<T> WithMcpActivity<T>(
        this ActivitySource source,
        string toolName,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(action);

        using var activity = source.StartActivity($"Mcp.Tool.{toolName}", ActivityKind.Server);
        activity?.SetTag(PeerServiceTag, "isestudio.mcp");
        activity?.SetTag(ToolTag, toolName);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(OutcomeTag, "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Tuple of a SHACL report and its measured wall-clock duration. Used by
/// <see cref="TelemetryExtensions.WithShaclActivity"/> so callers can record
/// the duration histogram without re-measuring.
/// </summary>
public readonly record struct ShaclReportWithDuration(ShaclReport Report, TimeSpan Duration);