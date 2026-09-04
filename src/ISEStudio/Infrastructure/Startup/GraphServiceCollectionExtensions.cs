using ISEStudio.Graph;

namespace ISEStudio.Infrastructure.Startup;

public static class GraphServiceCollectionExtensions
{
    public static IServiceCollection AddGraphStore(this IServiceCollection services)
    {
        services.AddScoped<IGraphStore, GraphStore>();
        return services;
    }
}