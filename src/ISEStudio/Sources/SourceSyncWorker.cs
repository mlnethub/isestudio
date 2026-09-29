using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources;

public sealed class SourceSyncWorkerOptions
{
    public int MaxConcurrency { get; set; } = 4;
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class SourceSyncWorker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SourceSyncJobStore _jobs;
    private readonly TimeProvider _clock;
    private readonly ILogger<SourceSyncWorker> _logger;
    private readonly SourceSyncWorkerOptions _options;
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    public SourceSyncWorker(
        IServiceScopeFactory scopeFactory,
        SourceSyncJobStore jobs,
        TimeProvider clock,
        ILogger<SourceSyncWorker> logger,
        IOptions<SourceSyncWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new SourceSyncWorkerOptions();
        if (_options.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Source sync concurrency must be positive.");
        if (_options.LeaseRenewInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Source sync lease interval must be positive.");
    }

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        var claim = await _jobs.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
        if (claim is null) return false;
        await ExecuteClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> DrainQueuedAsync(CancellationToken cancellationToken)
    {
        await _drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var active = new HashSet<Task>();
        var claimedCount = 0;
        try
        {
            while (true)
            {
                while (active.Count < _options.MaxConcurrency)
                {
                    var claim = await _jobs.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
                    if (claim is null) break;
                    active.Add(ExecuteClaimAsync(claim, cancellationToken));
                    claimedCount++;
                }

                if (active.Count == 0) return claimedCount;
                var completed = await Task.WhenAny(active).ConfigureAwait(false);
                active.Remove(completed);
                await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(active).ConfigureAwait(false);
            }
            finally
            {
                _drainGate.Release();
            }
        }
    }

    private async Task ExecuteClaimAsync(SourceSyncJobEntity claim, CancellationToken stoppingToken)
    {
        var runId = claim.ActiveRunId;
        if (!runId.HasValue) return;

        SourceSyncResult result;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SourceSyncCoordinator>();
            result = await RunWithRenewalAsync(coordinator, claim, runId.Value, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            result = new SourceSyncResult(0, 0, ["Source sync was cancelled."], IsComplete: false);
        }
        catch (Exception exception)
        {
            _logger.LogError("Source sync job {JobId} failed with {ErrorType}.",
                claim.Id, exception.GetType().Name);
            result = new SourceSyncResult(0, 0, ["Source sync failed."], IsComplete: false);
        }

        try
        {
            await _jobs.CompleteAsync(claim.Id, runId.Value, result, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError("Source sync completion for job {JobId} failed with {ErrorType}.",
                claim.Id, exception.GetType().Name);
        }
    }

    private async Task<SourceSyncResult> RunWithRenewalAsync(
        SourceSyncCoordinator coordinator,
        SourceSyncJobEntity claim,
        Guid runId,
        CancellationToken stoppingToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var execution = coordinator.RunAsync(claim.SourceId, claim.Id, runId, executionCancellation.Token);
        while (!execution.IsCompleted)
        {
            using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var renewalDelay = Task.Delay(
                _options.LeaseRenewInterval, _clock, renewalCancellation.Token);
            var completed = await Task.WhenAny(execution, renewalDelay).ConfigureAwait(false);
            if (completed != renewalDelay)
            {
                renewalCancellation.Cancel();
                try
                {
                    await renewalDelay.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
                {
                }
            }
            if (completed == execution) break;
            if (stoppingToken.IsCancellationRequested)
            {
                executionCancellation.Cancel();
                break;
            }

            try
            {
                if (await _jobs.RenewAsync(claim.Id, runId, stoppingToken).ConfigureAwait(false)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                executionCancellation.Cancel();
                break;
            }

            executionCancellation.Cancel();
            break;
        }

        try
        {
            return await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SourceSyncResult(0, 0, ["Source sync was cancelled or its lease was lost."],
                IsComplete: false);
        }
        catch (Exception exception)
        {
            _logger.LogError("Source sync job {JobId} execution failed with {ErrorType}.",
                claim.Id, exception.GetType().Name);
            return new SourceSyncResult(0, 0, ["Source sync failed."], IsComplete: false);
        }
    }
}