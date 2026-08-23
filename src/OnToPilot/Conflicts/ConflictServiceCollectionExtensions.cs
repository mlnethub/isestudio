using Microsoft.Extensions.DependencyInjection;

namespace OnToPilot.Conflicts;

/// <summary>
/// DI helpers for the conflicts slice. Mirrors <see cref="Providers.ProviderServiceCollectionExtensions"/>:
/// the service is Scoped (it depends on the scoped <c>OnToPilotDbContext</c>);
/// the optional <c>StoreWrapper</c> + <c>ExtractionJobStore</c> dependencies
/// are resolved per-request through <see cref="IServiceProvider"/> rather
/// than constructor-injected so the SQLite-backed contract-test factory can
/// run the SQL paths without an Oxigraph store.
/// </summary>
public static class ConflictServiceCollectionExtensions
{
    public static IServiceCollection AddConflictServices(this IServiceCollection services)
    {
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        return services;
    }
}