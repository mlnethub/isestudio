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
/// singleton: the orchestrator is a durable job-submission facade, and every
/// collaborator is either stateless or thread-safe. Job execution belongs to
/// <see cref="DurableExtractionWorker"/>.
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
        services.AddScoped<TerminologyService>();
        services.AddScoped<ITerminologySync>(sp => sp.GetRequiredService<TerminologyService>());
        services.AddSingleton<PromptSnapshotService>();
        services.AddScoped<IExtractionMerger, ExtractionMerger>();
        services.AddScoped<ExtractionOrchestrator>();
        services.AddScoped<TerminologyAgent>();
        services.AddScoped<IExtractionJobHandler, ParserExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, TBoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, ABoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, CombinedExtractionJobHandler>();
        services.PostConfigure<DurableExtractionWorkerOptions>(options =>
        {
            options.SupportedKinds = new[]
            {
                PlainTextIngestionJobProcessor.Kind,
                ExtractionWire.KindTBox,
                ExtractionWire.KindABox,
                ExtractionWire.KindBoth,
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