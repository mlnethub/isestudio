using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Sources;

public static class SourceServiceCollectionExtensions
{
    public static IServiceCollection AddSourceServices(this IServiceCollection services)
    {
        services.AddSingleton(new SourceKindDescriptor(
            SourceKind.Folder, ActiveSync: false, Array.Empty<SourceConfigField>()));
        services.AddSingleton<SourceAdapterRegistry>();
        services.AddSingleton<SourceSecretProtector>();
        services.AddSingleton<ISourceSecretProtector>(provider =>
            provider.GetRequiredService<SourceSecretProtector>());
        services.AddSingleton<SourceSyncJobStore>();
        services.AddScoped<SourceSyncCoordinator>();
        services.AddScoped<SourceService>();
        services.AddSingleton<SourceSyncWorker>();
        services.AddSingleton<SourceSyncScheduler>();
        return services;
    }
}