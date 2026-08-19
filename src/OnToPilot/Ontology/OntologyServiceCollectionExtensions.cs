using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }
}
