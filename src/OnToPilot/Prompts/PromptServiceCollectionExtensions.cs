using Microsoft.Extensions.DependencyInjection;

namespace OnToPilot.Prompts;

public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptService>();
        return services;
    }
}