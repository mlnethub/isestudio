using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Integration;
using ISEStudio.Integration;

namespace ISEStudio.Prompts;

public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptService>();
        // Application service facade for the four prompts.* dispatcher
        // arms (prompts.list + prompts.update + prompts.restore +
        // prompts.restore_all). Scoped — shares the request DbContext
        // with PromptService through the constructor.
        services.AddScoped<IPromptsApplicationService, PromptsApplicationService>();
        return services;
    }
}