using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Knowledge;

namespace OnToPilot.Ontology;

/// <summary>
/// DI helpers for the ontology slice. The service is Scoped (it
/// depends on the request-scoped <c>OnToPilotDbContext</c>). The
/// <see cref="OntologyEditor"/> and <see cref="StoreWrapper"/> are
/// singletons registered in <c>Program.cs</c> so the Oxigraph handle
/// is reused across requests and the write coordinator survives
/// HTTP-request boundaries.
/// </summary>
public static class OntologyServiceCollectionExtensions
{
    public static IServiceCollection AddOntologyServices(this IServiceCollection services)
    {
        services.AddScoped<OntologyService>();
        services.AddScoped<PublishedOntologyService>();
        services.AddScoped<ExternalOntologyService>();
        services.AddSingleton<OntologyViewBuilder>();
        // Refreshes the cached class/property/axiom count columns on
        // KnowledgeSystemEntity after TBox / ABox mutations. Scoped
        // because it shares the request DbContext with OntologyService
        // and ABoxService — the orchestrator path uses the scope
        // factory to materialize it on demand.
        services.AddScoped<KnowledgeStatsService>();
        // The release-typed ontology view reads the curated TBox shard
        // directly from disk, so it needs the artifact store. The store
        // lives under the same Storage:RdfRoot as the Oxigraph handle but
        // in a "releases" sibling directory so published shards and live
        // workspace data never collide.
        services.AddSingleton<ReleaseArtifactStore>(sp => new ReleaseArtifactStore(
            System.IO.Path.Combine(
                sp.GetRequiredService<IConfiguration>()["OnToPilot:Storage:RdfRoot"]
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data", "rdf"),
                "releases")));
        return services;
    }
}