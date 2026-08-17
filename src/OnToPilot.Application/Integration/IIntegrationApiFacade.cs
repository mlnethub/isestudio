using OnToPilot.Application.Foundation;

namespace OnToPilot.Application.Integration;

/// <summary>
/// Shared surface for every REST controller and every MCP tool. Both
/// transports adapt to <see cref="IIntegrationApiFacade"/> so the
/// business logic has exactly one implementation, and a parity break in
/// one transport cannot drift from the other. Real implementations land
/// across tasks 2-4 of the api-mcp plan; this file only fixes the
/// boundary so the contract tests can lock the parameter ordering.
/// </summary>
public interface IIntegrationApiFacade
{
    /// <summary>
    /// Return the current mutable TBox for the bound knowledge system as
    /// structured classes, properties, axioms, and labels. Used by both
    /// the internal <c>GET /api/.../ontology</c> endpoint and the MCP
    /// <c>get_ontology</c> tool.
    /// </summary>
    Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Run a bounded read-only SPARQL SELECT or ASK over the published
    /// graph of <paramref name="publicId"/>. Rejects SERVICE, FROM, GRAPH,
    /// and update verbs before touching the store. Used by the external
    /// API and by the MCP <c>query_knowledge</c> tool.
    /// </summary>
    Task<QueryResponse> QueryAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validate a structured ontology change-set and return its exact
    /// RDF diff without writing to the workspace. The caller applies the
    /// same operations through a follow-up tool/endpoint after the
    /// preview matches their expectations.
    /// </summary>
    Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken);
}