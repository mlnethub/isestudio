using ISEStudio.Application.Integration;
using ISEStudio.Documents;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Integration;
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
        services.PostConfigure<DurableExtractionWorkerOptions>(options =>
        {
            options.PollInterval = options.PollInterval < DurableExtractionWorkerOptions.MinPollInterval
                ? DurableExtractionWorkerOptions.MinPollInterval
                : options.PollInterval > DurableExtractionWorkerOptions.MaxPollInterval
                    ? DurableExtractionWorkerOptions.MaxPollInterval
                    : options.PollInterval;
        });
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
        services.AddScoped<IExtractionJobHandler, PlainTextExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, TBoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, ABoxExtractionJobHandler>();
        services.PostConfigure<DurableExtractionWorkerOptions>(options =>
        {
            options.SupportedKinds = new[]
            {
                PlainTextIngestionJobProcessor.Kind,
                ExtractionWire.KindTBox,
                ExtractionWire.KindABox,
            };
        });
        services.AddScoped<ExtractionJobDispatcher>();
        // Application service facade for the five extraction.* dispatcher
        // arms (three run* + list_jobs + get_job). Scoped — shares the
        // request DbContext with the BuildFrontendExtractionRequestAsync
        // provider / chunk resolution through the constructor.
        services.AddScoped<IExtractionApplicationService, ExtractionApplicationService>();
        services.AddDovetailPipelines();
        return services;
    }
}