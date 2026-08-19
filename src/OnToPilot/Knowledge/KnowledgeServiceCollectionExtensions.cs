using Microsoft.Extensions.DependencyInjection;

namespace OnToPilot.Knowledge;

/// <summary>
/// DI helper for the knowledge slice. Mirrors
/// <see cref="Conflicts.ConflictServiceCollectionExtensions"/>: the service
/// is Scoped (it depends on the scoped <c>OnToPilotDbContext</c>) so the
/// request-scoped entity tracker flows through.
/// </summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeServices(this IServiceCollection services)
    {
        services.AddScoped<KnowledgeService>();
        return services;
    }
}