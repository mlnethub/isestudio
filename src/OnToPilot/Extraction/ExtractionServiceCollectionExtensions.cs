using OnToPilot.Extraction;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Storage;

namespace OnToPilot.Extraction;

/// <summary>
/// DI registration for the extraction pipeline. All services are
/// singletons: the orchestrator must be singleton to maintain
/// <see cref="Task.Run"/> background-job state, and every collaborator is
/// either stateless or thread-safe.
/// </summary>
public static class ExtractionServiceCollectionExtensions
{
    public static IServiceCollection AddExtractionServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<EndpointCapacityCoordinator>();
        services.AddSingleton<TBoxExtractionService>();
        services.AddSingleton<ABoxExtractionService>();
        services.AddSingleton<TerminologyService>();
        services.AddSingleton<PromptSnapshotService>();
        services.AddSingleton<IExtractionMerger, ExtractionMerger>();
        services.AddSingleton<ExtractionOrchestrator>();
        services.AddScoped<TerminologyAgent>();
        return services;
    }
}