using OnToPilot.Application.Foundation;

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

    /// <summary>
    /// Compose the facade around an <see cref="IInternalOperationDispatcher"/>.
    /// The dispatcher is the per-operation routing table; in production it
    /// is wired by <c>Program.cs</c> against the live services
    /// (<c>OntologyEditor</c>, <c>ABoxManager</c>, <c>ReleaseManager</c>, …).
    /// </summary>
    public IntegrationApiFacade(IInternalOperationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
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
    public Task<QueryResponse> QueryAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken)
    {
        // Task 3 will wire the read-only SPARQL executor. For now return an
        // empty row set so the facade surface compiles; controllers will
        // never reach this path until task 3.
        return Task.FromResult(new QueryResponse(Array.Empty<IReadOnlyDictionary<string, object?>>()));
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