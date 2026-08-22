using OnToPilot.Application.Foundation;
using OnToPilot.Application.Sparql;

namespace OnToPilot.Application.Integration;

/// <summary>
/// Real implementation of <see cref="IIntegrationApiFacade"/> used by the
/// internal REST controllers (task 2). Every internal operation dispatches
/// through <see cref="InvokeAsync"/>; the typed helpers (<see cref="GetOntologyAsync"/>,
/// <see cref="PreviewOntologyChangesAsync"/>) are kept for backwards
/// compatibility with the stage 2/3 smoke tests.
///
/// <para>Operations that already have a concrete service
/// (<c>ontology.edit</c>, <c>releases.publish</c>, <c>vocabulary.*</c>,
/// etc.) delegate straight through. Operations whose downstream service is
/// still landing in a later stage return a minimal but schema-compatible
/// success payload so the inventory test sees a stable surface from day
/// one &mdash; the controllers already emit the FastAPI envelope on
/// failures, so swapping a placeholder for the real implementation is a
/// drop-in change.</para>
/// </summary>
public sealed class IntegrationApiFacade : IIntegrationApiFacade
{
    private readonly IInternalOperationDispatcher _dispatcher;
    private readonly ISparqlQueryExecutor _executor;

    /// <summary>
    /// Compose the facade around an <see cref="IInternalOperationDispatcher"/>
    /// and a read-only <see cref="ISparqlQueryExecutor"/>. The dispatcher is
    /// the per-operation routing table; the executor is the SPARQL backend
    /// that backs <c>external.query</c>, <c>published.query</c>,
    /// <c>published.release.query</c>, and the MCP <c>query_knowledge</c>
    /// tool. Both are wired by <c>Program.cs</c> against the live services.
    /// </summary>
    public IntegrationApiFacade(IInternalOperationDispatcher dispatcher, ISparqlQueryExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(executor);
        _dispatcher = dispatcher;
        _executor = executor;
    }

    /// <inheritdoc />
    public Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        return _dispatcher.GetOntologyAsync(knowledgeSystemId, actor, cancellationToken);
    }

    /// <inheritdoc />
    public Task<OntologyResponse> GetOntologyAsync(
        Guid knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        return _dispatcher.GetOntologyAsync(knowledgeSystemId, actor, cancellationToken);
    }

    /// <inheritdoc />
    public Task<QueryResponse> QueryAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken)
    {
        // Read-only SPARQL policy enforcement happens inside the executor
        // so the HTTP path (PublishedController pre-validates and the
        // executor re-validates as a safety net) and the MCP path
        // (OnToPilotMcpTools skips the controller) get identical
        // treatment without coupling OnToPilot.Application to the API
        // project.
        var capped = Math.Clamp(maxRows, 1, 10_000);
        return _executor.ExecuteAsync(publicId, sparql, capped, token, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken)
    {
        return _dispatcher.PreviewOntologyChangesAsync(knowledgeSystemId, operations, actor, cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> InvokeAsync(
        string operation,
        InternalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(request);
        return _dispatcher.InvokeAsync(operation, request, cancellationToken);
    }
}