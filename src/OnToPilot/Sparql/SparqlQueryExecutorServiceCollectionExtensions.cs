using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Sparql;

namespace OnToPilot.Sparql;

public static class SparqlQueryExecutorServiceCollectionExtensions
{
    public static IServiceCollection AddSparqlServices(this IServiceCollection services)
    {
        services.AddScoped<ISparqlQueryExecutor, SparqlQueryExecutor>();
        return services;
    }
}
