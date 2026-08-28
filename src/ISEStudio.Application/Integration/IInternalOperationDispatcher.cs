using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Routing table for the internal REST surface. Each entry knows how to
/// handle one operation name (the same string the OpenAPI inventory uses
/// to identify the route, e.g. <c>"ontology.get"</c>,
/// <c>"releases.publish"</c>) and return the success payload the controller
/// will serialise verbatim.
///
/// <para>The dispatcher keeps <see cref="IIntegrationApiFacade"/> itself a
/// pure protocol adapter: adding a new internal operation is a single
/// registration here, not a new method on the facade. The former stage
/// 2/3 typed helpers (<c>GetOntologyAsync</c>,
/// <c>PreviewOntologyChangesAsync</c>) were removed in the facade
/// de-coupling pass — the facade now reaches
/// <see cref="IOntologyApplicationService"/> and the SPARQL executor
/// directly instead of forwarding through the dispatcher, which also
/// removed the facade↔dispatcher mutual reference (the dispatcher used
/// to resolve <see cref="IIntegrationApiFacade"/> for the SPARQL query
/// arms).</para>
/// </summary>
public interface IInternalOperationDispatcher
{
    /// <summary>Route an internal operation by stable name.</summary>
    Task<object?> InvokeAsync(
        string operation,
        InternalRequest request,
        CancellationToken cancellationToken);
}
