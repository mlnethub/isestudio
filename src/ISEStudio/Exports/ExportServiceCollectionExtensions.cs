using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Exports;

/// <summary>
/// DI registration for the exports slice (slice 7b).
///
/// <list type="bullet">
///   <item><see cref="ExportJobStore"/>: singleton — opens fresh
///   <c>ISEStudioDbContext</c> per call via the registered
///   <see cref="IDbContextFactory{TContext}"/>.</item>
///   <item><see cref="ExportRunner"/>: singleton — pure background
///   worker; depends on the singleton <see cref="ExportJobStore"/> and
///   the optional singleton <see cref="Ontology.StoreWrapper"/>.</item>
///   <item><see cref="ExportService"/>: scoped — shares the request
///   <c>ISEStudioDbContext</c> with the dispatcher arm.</item>
/// </list>
/// </summary>
public static class ExportServiceCollectionExtensions
{
    public static IServiceCollection AddExportServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ExportJobStore>();
        services.AddSingleton<ExportRunner>();
        services.AddScoped<ExportService>();
        return services;
    }
}