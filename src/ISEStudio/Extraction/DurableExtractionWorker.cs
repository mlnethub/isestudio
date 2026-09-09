using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ISEStudio.Infrastructure.Persistence.Entities;
namespace ISEStudio.Extraction;

public sealed class DurableExtractionWorkerOptions
{
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public string[] SupportedKinds { get; set; } = Array.Empty<string>();
}

public class DurableExtractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExtractionJobStore _jobs;
    private readonly TimeProvider _clock;
    private readonly DurableExtractionWorkerOptions _options;
    private readonly HashSet<string>? _supportedKinds;
    private readonly ILogger<DurableExtractionWorker> _logger;

    public DurableExtractionWorker(
        IServiceScopeFactory scopeFactory,
        ExtractionJobStore jobs,
        TimeProvider clock,
        ILogger<DurableExtractionWorker> logger,
        IOptions<DurableExtractionWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
        _options = options?.Value ?? new DurableExtractionWorkerOptions();
        _supportedKinds = _options.SupportedKinds.Length == 0
            ? null
            : new HashSet<string>(_options.SupportedKinds, StringComparer.Ordinal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Track the claimed job outside the try so the catch block can
            // release the 409 guard for a dispatch that failed part-way.
            ExtractionJobEntity? claimedJob = null;
            try
            {
                claimedJob = await _jobs
                    .ClaimNextAsync(stoppingToken, kind: null)
                    .ConfigureAwait(false);
                if (claimedJob is null)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_supportedKinds is not null && !_supportedKinds.Contains(claimedJob.Kind))
                {
                    await _jobs.MarkFailedAsync(
                        claimedJob.Id,
                        $"Unsupported durable extraction kind '{claimedJob.Kind}'.",
                        stoppingToken).ConfigureAwait(false);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var scopedDispatcher = scope.ServiceProvider.GetRequiredService<ExtractionJobDispatcher>();
                await scopedDispatcher.DispatchAsync(claimedJob, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown — propagate so the BackgroundService exits
                // cleanly instead of falling into the failure path below.
                throw;
            }
            catch (Exception exception)
            {
                // Any other failure must not escape: the default
                // BackgroundServiceExceptionBehavior is StopHost, so an
                // unhandled exception here (DI resolution missing a
                // Dovetail factory, a lost database connection, a
                // job-level cancellation token) kills the whole API host
                // and leaves the claimed row stuck in `running` — the 409
                // active-job guard stays locked until the next boot's
                // stale-job recovery. Marking the job failed mirrors the
                // handled-failure path in ExtractionJobDispatcher: the
                // guard releases at failure time, exactly when no live
                // extraction remains.
                _logger.LogError(
                    exception,
                    "Durable extraction loop failed while dispatching job {JobId}; marking it failed",
                    claimedJob?.Id);

                if (claimedJob is not null)
                {
                    try
                    {
                        await _jobs.MarkFailedAsync(
                            claimedJob.Id,
                            $"{exception.GetType().Name}: {exception.Message}",
                            stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception markFailure)
                    {
                        // Best effort — a failed transition must not
                        // re-escalate into a host stop.
                        _logger.LogError(
                            markFailure,
                            "Failed to mark durable extraction job {JobId} as failed",
                            claimedJob.Id);
                    }
                }

                // Back off one poll interval so a persistent fault (e.g.
                // the database is unreachable) does not spin the loop hot.
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}