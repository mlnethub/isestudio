using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Integration;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Compile-time smoke test for <see cref="IIntegrationApiFacade"/>. The
/// stage-2/3 typed helpers (<see cref="IntegrationApiFacade.GetOntologyAsync"/>,
/// <see cref="IntegrationApiFacade.PreviewOntologyChangesAsync"/>) still
/// return placeholder payloads; the test asserts that the facade surface
/// compiles, can be instantiated, and surfaces <see cref="NotSupportedException"/>
/// when an unrecognised internal operation is dispatched (task 2 covers
/// every operation the contract enumerates, so any new NotSupportedException
/// is a regression).
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class FacadeSmokeTests
{
    private static IIntegrationApiFacade BuildFacade()
    {
        // Build a minimal dispatcher with no backing services. The smoke
        // tests never exercise the dispatcher (they assert the typed
        // surface returns its stub values), so the empty implementation
        // is sufficient.
        var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new InternalOperationDispatcher(services);
        return new IntegrationApiFacade(dispatcher);
    }

    /// <summary>
    /// Verifies that <see cref="IntegrationApiFacade"/> satisfies
    /// <see cref="IIntegrationApiFacade"/> and that
    /// <see cref="IIntegrationApiFacade.GetOntologyAsync"/> returns an
    /// empty (but non-throwing) <see cref="OntologyResponse"/>.
    /// </summary>
    [Fact]
    public async Task Facade_interface_compiles_and_get_ontology_returns_placeholder()
    {
        IIntegrationApiFacade facade = BuildFacade();
        var response = await facade.GetOntologyAsync(1, new Actor("system"), CancellationToken.None);
        Assert.NotNull(response);
        Assert.Empty(response.Classes);
        Assert.Empty(response.Properties);
    }

    /// <summary>
    /// Verifies that <see cref="IIntegrationApiFacade.QueryAsync"/>
    /// returns an empty row set (task 3 owns the real SPARQL executor).
    /// </summary>
    [Fact]
    public async Task Facade_query_async_returns_empty_row_set()
    {
        IIntegrationApiFacade facade = BuildFacade();
        var token = new TokenPrincipal(
            TokenId: "test-token",
            KnowledgeSystemPublicId: "demo",
            Scopes: new[] { "knowledge:read" });
        var response = await facade.QueryAsync("demo", "SELECT * WHERE { ?s ?p ?o }", maxRows: 10, token, CancellationToken.None);
        Assert.NotNull(response);
        Assert.Empty(response.Rows);
    }

    /// <summary>
    /// Verifies that <see cref="IIntegrationApiFacade.PreviewOntologyChangesAsync"/>
    /// returns an empty preview placeholder.
    /// </summary>
    [Fact]
    public async Task Facade_preview_ontology_changes_async_returns_empty_preview()
    {
        IIntegrationApiFacade facade = BuildFacade();
        var operations = new[]
        {
            new EditOperation("AddClass", "https://example.test/A"),
        };
        var preview = await facade.PreviewOntologyChangesAsync(1, operations, new Actor("system"), CancellationToken.None);
        Assert.NotNull(preview);
        Assert.Empty(preview.AddedTriples);
        Assert.Empty(preview.RemovedTriples);
    }

    /// <summary>
    /// Verifies that an unrecognised internal operation surfaces
    /// <see cref="NotSupportedException"/> so a controller cannot silently
    /// regress the contract by introducing a typo in the dispatcher.
    /// </summary>
    [Fact]
    public async Task Facade_invoke_throws_for_unknown_operation()
    {
        IIntegrationApiFacade facade = BuildFacade();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            facade.InvokeAsync(
                "nonsense.operation",
                new InternalRequest(null, null, null, null, null, null, new Actor("system")),
                CancellationToken.None));
    }
}