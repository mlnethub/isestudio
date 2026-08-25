using ISEStudio.Extraction;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Storage;

namespace ISEStudio.Extraction;

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
        services.AddSingleton<TBoxVerifyService>();
        services.AddSingleton<CorpusRecoveryService>();
        services.AddSingleton<HierarchyRecoveryService>();
        services.AddSingleton<ABoxExtractionService>();
        services.AddSingleton<TerminologyService>();
        services.AddSingleton<ITerminologySync>(sp => sp.GetRequiredService<TerminologyService>());
        services.AddSingleton<PromptSnapshotService>();
        services.AddSingleton<IExtractionMerger, ExtractionMerger>();
        services.AddSingleton<ExtractionOrchestrator>();
        services.AddScoped<TerminologyAgent>();
        return services;
    }
}