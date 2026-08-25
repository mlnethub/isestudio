using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.EntityResolution;

public static class ResolutionServiceCollectionExtensions
{
    public static IServiceCollection AddResolutionServices(this IServiceCollection services)
    {
        services.AddScoped<ResolutionService>();
        return services;
    }
}