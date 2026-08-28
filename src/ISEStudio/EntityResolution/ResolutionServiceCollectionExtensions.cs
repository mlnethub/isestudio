using ISEStudio.Application.Integration;
using ISEStudio.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.EntityResolution;

public static class ResolutionServiceCollectionExtensions
{
    public static IServiceCollection AddResolutionServices(this IServiceCollection services)
    {
        services.AddScoped<ResolutionService>();
        // Application service facade for the five resolution.*
        // dispatcher arms (get_queue + list_decisions + resolve +
        // revoke_decision + edit_decision_reason). Scoped — shares
        // the request DbContext with ResolutionService through the
        // constructor.
        services.AddScoped<IResolutionApplicationService, ResolutionApplicationService>();
        return services;
    }
}