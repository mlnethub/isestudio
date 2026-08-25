using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Prompts;

public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptService>();
        return services;
    }
}