using Microsoft.Extensions.Hosting;

namespace ISEStudio.Documents;

public sealed class DocumentParseWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DocumentParseJobStore _jobs;
    private readonly ILogger<DocumentParseWorker> _logger;

    public DocumentParseWorker(IServiceScopeFactory scopes, DocumentParseJobStore jobs,
        ILogger<DocumentParseWorker> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? claimedId = null;
            try
            {
                var job = await _jobs.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
                    continue;
                }
                claimedId = job.Id;
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DocumentParseJobProcessor>()
                    .ProcessAsync(job.Id, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (claimedId is { } jobId)
                    await _jobs.MarkFailedAsync(jobId, "Document parsing was interrupted.", CancellationToken.None)
                        .ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Document parse job {JobId} failed", claimedId);
                if (claimedId is { } jobId)
                {
                    try
                    {
                        await _jobs.MarkFailedAsync(jobId, exception.Message, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception markError)
                    {
                        _logger.LogError(markError, "Could not mark document parse job {JobId} failed", jobId);
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}