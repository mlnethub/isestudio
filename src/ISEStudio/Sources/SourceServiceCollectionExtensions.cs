using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Sources.Adapters;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ISEStudio.Sources;

public static class SourceServiceCollectionExtensions
{
    public static IServiceCollection AddSourceServices(this IServiceCollection services)
    {
        services.AddSingleton(new SourceKindDescriptor(
            SourceKind.Folder, ActiveSync: false, Array.Empty<SourceConfigField>()));
        services.AddSingleton(new SourceKindDescriptor(
            SourceKind.Api, ActiveSync: false, Array.Empty<SourceConfigField>()));
        services.AddScoped<SourcePushService>();
        services.AddScoped<SourceStatementService>();
        services.AddSingleton(new SourceKindDescriptor(SourceKind.Statements, ActiveSync: false, Array.Empty<SourceConfigField>()));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.Pragma = "no-cache";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { detail = "Source push rate limit exceeded" }, ct).ConfigureAwait(false);
            };
            options.AddPolicy(SourcePushService.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                (Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var ksId) ? ksId : Guid.Empty,
                    Guid.TryParse(context.Request.RouteValues["sourceId"]?.ToString(), out var sourceId) ? sourceId : Guid.Empty,
                    context.Connection.RemoteIpAddress?.ToString()),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
                }));
        });
        foreach (var descriptor in HttpSourceConfig.Descriptors)
            services.AddSingleton(descriptor);
        foreach (var descriptor in ObjectStorageSourceConfig.Descriptors)
            services.AddSingleton(descriptor);
        foreach (var descriptor in ContentSourceConfig.Descriptors)
            services.AddSingleton(descriptor);
        services.AddScoped<ISourceAdapter, WebDavSourceAdapter>();
        services.AddScoped<ISourceAdapter, NotionSourceAdapter>();
        services.AddSingleton<IS3SourceClientFactory, S3SourceClientFactory>();
        services.AddSingleton<IGcsSourceClientFactory, GcsSourceClientFactory>();
        services.AddScoped<ISourceAdapter, S3SourceAdapter>();
        services.AddScoped<ISourceAdapter, GcsSourceAdapter>();
        services.AddScoped<ISourceAdapter, UrlSourceAdapter>();
        services.AddScoped<ISourceAdapter, RssSourceAdapter>();
        services.AddScoped<ISourceAdapter, CustomSourceAdapter>();
        services.AddScoped<ISourceAdapter, GitHubIssuesSourceAdapter>();
        services.AddScoped<ISourceAdapter, JiraIssuesSourceAdapter>();
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