using ISEStudio.Graph;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Repositories;

namespace ISEStudio.Infrastructure.Startup;

public static class GraphServiceCollectionExtensions
{
    public static IServiceCollection AddGraphStore(this IServiceCollection services)
    {
        services.AddScoped<IPostgresGraphRepository, PostgresGraphRepository>();
        services.AddScoped<IGraphStore>(serviceProvider =>
            new GraphStore(
                serviceProvider.GetRequiredService<ISEStudioDbContext>(),
                serviceProvider.GetRequiredService<IPostgresGraphRepository>()));
        return services;
    }
}