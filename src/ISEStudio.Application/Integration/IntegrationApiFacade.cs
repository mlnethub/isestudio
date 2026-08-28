using ISEStudio.Application.Foundation;
using ISEStudio.Application.Sparql;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Real implementation of <see cref="IIntegrationApiFacade"/> used by the
/// internal REST controllers, the external / published API, and the MCP
/// transport. Every internal operation dispatches through
/// <see cref="InvokeAsync"/>; the typed helpers are thin adapters over
/// their concrete collaborators instead of forwarding through the
/// dispatcher:
///
/// <list type="bullet">
/// <item><see cref="GetOntologyAsync(Guid, Actor, CancellationToken)"/>
/// reuses the 6/13 <see cref="IOntologyApplicationService.GetAsync"/>
/// path so the typed facade and the <c>ontology.get</c> arm share one
/// implementation.</item>
/// <item><see cref="QueryAsync"/> delegates to the read-only
/// <see cref="ISparqlQueryExecutor"/> (the dispatcher's
/// <c>external.query</c> / <c>published.query</c> arms route through the
/// application services to the same executor).</item>
/// <item><see cref="GetOntologyAsync(long, Actor, CancellationToken)"/>
/// and <see cref="PreviewOntologyChangesAsync"/> remain stage-placeholder
/// stubs for the out-of-scope MCP callers and the smoke tests.</item>
/// </list>
///
/// The facade therefore depends on the dispatcher in one direction only
/// (for <see cref="InvokeAsync"/>); nothing inside the dispatcher resolves
/// the facade any more, which removes the historical facade↔dispatcher
/// mutual reference.
/// </summary>
public sealed class IntegrationApiFacade : IIntegrationApiFacade
{
    private readonly IInternalOperationDispatcher _dispatcher;
    private readonly ISparqlQueryExecutor _executor;
    private readonly IOntologyApplicationService _ontology;

    /// <summary>
    /// Compose the facade around an <see cref="IInternalOperationDispatcher"/>
    /// (per-operation routing table), a read-only
    /// <see cref="ISparqlQueryExecutor"/> (SPARQL backend for the query
    /// surface + the MCP <c>query_knowledge</c> tool), and the
    /// <see cref="IOntologyApplicationService"/> (typed
    /// <see cref="GetOntologyAsync(Guid, Actor, CancellationToken)"/>
    /// path). All three are wired by <c>Program.cs</c> against the live
    /// services.
    /// </summary>
    public IntegrationApiFacade(
        IInternalOperationDispatcher dispatcher,
        ISparqlQueryExecutor executor,
        IOntologyApplicationService ontology)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(ontology);
        _dispatcher = dispatcher;
        _executor = executor;
        _ontology = ontology;
    }

    /// <inheritdoc />
    public Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        // Stage placeholder for the out-of-scope MCP get_ontology caller
        // (it currently passes a hard-coded id). Kept returning an empty
        // TBox so the typed surface still compiles and the smoke test
        // sees a non-throwing result.
        return Task.FromResult(EmptyOntologyResponse());
    }

    /// <inheritdoc />
    public async Task<OntologyResponse> GetOntologyAsync(
        Guid knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        var request = new InternalRequest(
            KnowledgeSystemId: null,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: null,
            Actor: actor,
            KnowledgeSystemGuid: knowledgeSystemId);
        var view = await _ontology.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (view is null)
            throw new KeyNotFoundException($"Knowledge system {knowledgeSystemId} not found.");
        return view;
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
        // (ISEStudioMcpTools skips the controller) get identical
        // treatment without coupling ISEStudio.Application to the API
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
        // Stage placeholder for the MCP preview_ontology_changes caller.
        return Task.FromResult(new ChangePreview(
            AddedTriples: Array.Empty<string>(),
            RemovedTriples: Array.Empty<string>()));
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

    private static OntologyResponse EmptyOntologyResponse() => new(
        Classes: Array.Empty<OntologyClass>(),
        ObjectProperties: Array.Empty<OntologyProperty>(),
        DataProperties: Array.Empty<OntologyProperty>(),
        Axioms: new OntologyAxioms(
            SubclassOf: Array.Empty<SubclassAxiom>(),
            DisjointWith: Array.Empty<PairAxiom>(),
            EquivalentClass: Array.Empty<PairAxiom>()),
        Labels: new Dictionary<string, string>(),
        Stats: new OntologyStats(0, 0, 0),
        KnowledgeSystem: null);
}
