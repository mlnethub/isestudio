using System.Diagnostics;

namespace ISEStudio.Migration.Ontology;

/// <summary>
/// Migration-local telemetry shim. Replaces the runtime's
/// <c>ISEStudio.Observability.Telemetry</c> so the migration tool does
/// not have to take a project reference on ISEStudio. The shim exposes
/// the same surface the StoreWrapper activity blocks consult: an
/// <see cref="ActivitySource"/> named <c>ISEStudio.Rdf</c> + a
/// <c>WithRdfActivity</c> helper that mirrors the runtime signature
/// (operation, graph, body, cancellation).
/// </summary>
internal static class Telemetry
{
    public static readonly ActivitySource RdfSource = new("ISEStudio.Rdf");

    public static async ValueTask<T> WithRdfActivity<T>(
        string operation,
        string? graphIri,
        Func<CancellationToken, ValueTask<T>> body,
        CancellationToken cancellationToken = default)
    {
        using var activity = RdfSource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag(TelemetryExtensions.PeerServiceTag, "oxigraph");
        activity?.SetTag(TelemetryExtensions.OperationTag, operation);
        if (!string.IsNullOrEmpty(graphIri))
        {
            activity?.SetTag(TelemetryExtensions.GraphTag, graphIri);
        }

        try
        {
            var result = await body(cancellationToken).ConfigureAwait(false);
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

internal static class TelemetryExtensions
{
    public const string PeerServiceTag = "peer.service";
    public const string OperationTag = "operation.name";
    public const string GraphTag = "rdf.graph";
    public const string QuadCountTag = "rdf.quad_count";
    public const string OutcomeTag = "outcome";
}
