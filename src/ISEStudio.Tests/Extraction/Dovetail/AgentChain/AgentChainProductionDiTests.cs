using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Knowledge;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

/// <summary>
/// Verifies the Task 5 R1 fix: the REAL production extension methods
/// (<see cref="ConflictServiceCollectionExtensions.AddConflictServices"/> /
/// <see cref="OntologyServiceCollectionExtensions.AddOntologyServices"/>)
/// register the three agent-chain interface keys
/// (<c>IConflictAgent</c> / <c>IStructureAgent</c> / <c>IKnowledgeStatsService</c>)
/// as forwarders to the scoped concrete instances. Without these, the
/// Dovetail agent-chain steps and the orchestrator fallback resolve null in
/// production and the chain silently no-ops (Task 5 report concern #1,
/// spec §5 D6 assumed the registrations existed).
///
/// <para>The interface keys are NOT registered by these tests — resolving
/// them proves the production extension methods do the job. MS.DI
/// registrations are lazy (no validate-on-build), so the heavy graphs inside
/// the extension methods are never activated; only the resolved branch
/// constructs. Fixture mirrors
/// <see cref="ExtractionAgentChainTests.BuildServices"/>: SQLite in-memory
/// DbContext, FakeChatClientFactory, TimeProvider.System, options, and an
/// in-memory Oxigraph <see cref="StoreWrapper"/> for the stats service.</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class AgentChainProductionDiTests : IDisposable
{
    private readonly string _root;
    private readonly StoreWrapper _store;
    private readonly SqliteContextFactory _contexts;

    public AgentChainProductionDiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-agent-chain-di-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _store = new StoreWrapper(Path.Combine(_root, "store"));
        _contexts = new SqliteContextFactory();
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AddConflictServices_RegistersIConflictAgent()
    {
        using var sp = BuildServices(services => services.AddConflictServices());
        using var scope = sp.CreateScope();

        var agent = scope.ServiceProvider.GetService<IConflictAgent>();
        Assert.NotNull(agent);
        // The forwarder must resolve the REAL scoped concrete, not a fake.
        Assert.IsType<ConflictAgent>(agent);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AddOntologyServices_RegistersIStructureAgent()
    {
        using var sp = BuildServices(services => services.AddOntologyServices());
        using var scope = sp.CreateScope();

        var agent = scope.ServiceProvider.GetService<IStructureAgent>();
        Assert.NotNull(agent);
        Assert.IsType<StructureAgent>(agent);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AddOntologyServices_RegistersIKnowledgeStatsService()
    {
        using var sp = BuildServices(services => services.AddOntologyServices());
        using var scope = sp.CreateScope();

        var stats = scope.ServiceProvider.GetService<IKnowledgeStatsService>();
        Assert.NotNull(stats);
        // KnowledgeStatsService needs StoreWrapper (fixture) + OntologyViewBuilder
        // (registered by AddOntologyServices itself).
        Assert.IsType<KnowledgeStatsService>(stats);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Minimal container that satisfies the agents' ctors (mirrors
    /// <c>ExtractionAgentChainTests.BuildServices</c>). The production
    /// extension method under test is applied via
    /// <paramref name="configure"/>; nothing else registers the interface
    /// keys.
    /// </summary>
    private ServiceProvider BuildServices(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(_store);
        configure(services);
        return services.BuildServiceProvider();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _contexts.Dispose();
        _store.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The Oxigraph handle can linger briefly on Windows; a stale
            // temp directory must never fail a test run.
        }
    }
}
