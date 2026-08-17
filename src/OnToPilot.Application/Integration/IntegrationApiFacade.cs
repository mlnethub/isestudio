using OnToPilot.Application.Foundation;

namespace OnToPilot.Application.Integration;

/// <summary>
/// Stub implementation of <see cref="IIntegrationApiFacade"/> used by
/// task 1's compile-time smoke test. Every method throws
/// <see cref="NotImplementedException"/>; the real wiring lives in tasks
/// 2 (REST controllers), 3 (external / published API), and 4 (MCP
/// transport + live authorization). Keeping the stub in the project
/// means tasks 2-4 only need to swap it for a real implementation — no
/// re-plumbing of constructor injection.
/// </summary>
public sealed class IntegrationApiFacade : IIntegrationApiFacade
{
    // TODO: implement in Task 2 (GetOntologyAsync, PreviewOntologyChangesAsync).
    public Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException(
            $"IntegrationApiFacade.GetOntologyAsync(ks={knowledgeSystemId}, actor={actor.UserId}) " +
            "will be implemented in Task 2 of the api-mcp plan.");
    }

    // TODO: implement in Task 3 (QueryAsync — external / published API).
    public Task<QueryResponse> QueryAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException(
            $"IntegrationApiFacade.QueryAsync(publicId={publicId}, maxRows={maxRows}, token={token.TokenId}) " +
            "will be implemented in Task 3 of the api-mcp plan.");
    }

    // TODO: implement in Task 2 (PreviewOntologyChangesAsync — internal API and MCP).
    public Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException(
            $"IntegrationApiFacade.PreviewOntologyChangesAsync(ks={knowledgeSystemId}, ops={operations.Count}, actor={actor.UserId}) " +
            "will be implemented in Task 2 of the api-mcp plan.");
    }
}