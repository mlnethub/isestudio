using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Search;

namespace ISEStudio.Infrastructure.Search;

public static class SearchServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresSearch(this IServiceCollection services)
    {
        services.AddScoped<ISearchIndex, PostgresSearchIndex>();
        return services;
    }
}