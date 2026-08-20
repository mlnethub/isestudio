using OnToPilot.Application.Foundation;

namespace OnToPilot.Application.Integration;

/// <summary>
/// Single entry point shared by the internal REST controllers (task 2),
/// the external / published API (task 3), and the MCP transport (task 4).
/// Every transport-specific concern (HTTP, JSON envelope, SPARQL scoping)
/// lives in the calling layer; the facade is purely protocol-agnostic.
/// </summary>
public interface IIntegrationApiFacade
{
    /// <summary>
    /// Fetch the structured TBox for the bound knowledge system. Used by
    /// <c>GET /api/knowledge/{ks_id}/ontology</c> and the MCP <c>get_ontology</c>
    /// tool.
    /// </summary>
    Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Guid-keyed overload for the migrated internal <c>ontology.get</c>
    /// surface. The <c>long</c> overload above is retained for the
    /// out-of-scope MCP <c>get_ontology</c> caller and will be removed when
    /// the Stage 2 placeholder is filled.
    /// </summary>
    Task<OntologyResponse> GetOntologyAsync(
        Guid knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Run a read-only SPARQL query against the named knowledge system's
    /// published graph. Used by the external API
    /// (<c>POST /api/v1/knowledge-systems/{public_id}/query</c>); task 3 owns
    /// the read-only-policy enforcement.
    /// </summary>
    Task<QueryResponse> QueryAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken);

    /// <summary>
    /// Preview a structured TBox edit-set against the bound knowledge system.
    /// Returns the exact RDF diff the caller would commit if the operations
    /// were applied, without mutating the workspace. Used by both the
    /// internal <c>POST /api/knowledge/{ks_id}/ontology/edit</c> and the
    /// MCP <c>preview_ontology_changes</c> tool.
    /// </summary>
    Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Dispatch any internal REST operation by its stable name (e.g.
    /// <c>"ontology.get"</c>, <c>"releases.publish"</c>). Returns the success
    /// payload the controller should serialise verbatim, or throws to let
    /// the controller convert the failure into the FastAPI envelope.
    /// </summary>
    /// <param name="operation">
    /// Stable operation name. The internal API contract test enumerates the
    /// 154 names from the frozen Python OpenAPI baseline; controllers pick
    /// one and call through.
    /// </param>
    /// <param name="request">
    /// All controller inputs (KS id, public id, resource ids, body, query,
    /// actor) bundled into a single record so adding a new parameter does
    /// not require touching every caller.
    /// </param>
    /// <param name="cancellationToken">Forwarded from the request scope.</param>
    Task<object?> InvokeAsync(
        string operation,
        InternalRequest request,
        CancellationToken cancellationToken);
}