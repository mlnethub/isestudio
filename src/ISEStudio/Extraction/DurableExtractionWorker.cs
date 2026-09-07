using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ISEStudio.Extraction;

public sealed class DurableExtractionWorkerOptions
{
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

public class DurableExtractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExtractionJobStore _jobs;
    private readonly TimeProvider _clock;
    private readonly DurableExtractionWorkerOptions _options;

    public DurableExtractionWorker(
        IServiceScopeFactory scopeFactory,
        ExtractionJobStore jobs,
        TimeProvider clock,
        IOptions<DurableExtractionWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _clock = clock;
        _options = options?.Value ?? new DurableExtractionWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _jobs.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
            if (job is null)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scopedDispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();
                await scopedDispatcher.DispatchAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                try
                {
                    await _jobs.MarkFailedAsync(job.Id, exception.Message, stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}