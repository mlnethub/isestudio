using OnToPilot.Application.Foundation;

namespace OnToPilot.Application.Integration;

/// <summary>
/// Routing table for the internal REST surface. Each entry knows how to
/// handle one operation name (the same string the OpenAPI inventory uses
/// to identify the route, e.g. <c>"ontology.get"</c>,
/// <c>"releases.publish"</c>) and return the success payload the controller
/// will serialise verbatim.
///
/// <para>The dispatcher keeps <see cref="IIntegrationApiFacade"/> itself a
/// pure protocol adapter: adding a new internal operation is a single
/// registration here, not a new method on the facade. The stage 2/3 typed
/// helpers (<c>GetOntologyAsync</c>, <c>PreviewOntologyChangesAsync</c>) are
/// preserved on the facade for backwards compatibility with the smoke
/// tests.</para>
/// </summary>
public interface IInternalOperationDispatcher
{
    /// <summary>Route an internal operation by stable name.</summary>
    Task<object?> InvokeAsync(
        string operation,
        InternalRequest request,
        CancellationToken cancellationToken);

    /// <summary>Stage 2/3 helper kept on the dispatcher for the typed facade surface.</summary>
    Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken);

    /// <summary>Stage 2/3 helper kept on the dispatcher for the typed facade surface.</summary>
    Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken);
}