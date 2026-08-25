using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Sparql;

namespace ISEStudio.Sparql;

public static class SparqlQueryExecutorServiceCollectionExtensions
{
    public static IServiceCollection AddSparqlServices(this IServiceCollection services)
    {
        services.AddScoped<ISparqlQueryExecutor, SparqlQueryExecutor>();
        return services;
    }
}
