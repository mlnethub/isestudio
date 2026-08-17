using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Compile-time smoke test for <see cref="IIntegrationApiFacade"/>. The
/// stub <see cref="IntegrationApiFacade"/> is intentionally
/// not-implemented; the test asserts that the facade surface compiles,
/// can be instantiated, and surfaces <see cref="NotImplementedException"/>
/// from every method so future implementations cannot silently regress
/// the contract.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class FacadeSmokeTests
{
    /// <summary>
    /// Verifies that <see cref="IntegrationApiFacade"/> satisfies
    /// <see cref="IIntegrationApiFacade"/> and that
    /// <see cref="IIntegrationApiFacade.GetOntologyAsync"/> throws
    /// <see cref="NotImplementedException"/> until task 2 implements
    /// it. The other two methods are covered by separate facts so a
    /// single regression cannot mask the other paths.
    /// </summary>
    [Fact]
    public async Task Facade_interface_compiles_and_stub_throws_not_implemented()
    {
        IIntegrationApiFacade facade = new IntegrationApiFacade();
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            facade.GetOntologyAsync(1, new Actor("system"), CancellationToken.None));
    }

    /// <summary>
    /// Verifies that <see cref="IIntegrationApiFacade.QueryAsync"/>
    /// throws <see cref="NotImplementedException"/> until task 3
    /// implements it.
    /// </summary>
    [Fact]
    public async Task Facade_query_async_throws_not_implemented()
    {
        IIntegrationApiFacade facade = new IntegrationApiFacade();
        var token = new TokenPrincipal(
            TokenId: "test-token",
            KnowledgeSystemPublicId: "demo",
            Scopes: new[] { "knowledge:read" });
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            facade.QueryAsync("demo", "SELECT * WHERE { ?s ?p ?o }", maxRows: 10, token, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that <see cref="IIntegrationApiFacade.PreviewOntologyChangesAsync"/>
    /// throws <see cref="NotImplementedException"/> until task 2
    /// implements it.
    /// </summary>
    [Fact]
    public async Task Facade_preview_ontology_changes_async_throws_not_implemented()
    {
        IIntegrationApiFacade facade = new IntegrationApiFacade();
        var operations = new[]
        {
            new EditOperation("AddClass", "https://example.test/A"),
        };
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            facade.PreviewOntologyChangesAsync(1, operations, new Actor("system"), CancellationToken.None));
    }
}