using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

namespace ISEStudio.Sources;

public interface ISourceSyncQueueWakeup
{
    Task WakeAsync(CancellationToken cancellationToken);
}

public sealed class TickerQSourceSyncQueueWakeup(
    ITimeTickerManager<TimeTickerEntity> ticker,
    ILogger<TickerQSourceSyncQueueWakeup> logger) : ISourceSyncQueueWakeup
{
    public async Task WakeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await ticker.AddAsync(new TimeTickerEntity
            {
                Function = SourceSyncTickerFunctions.DrainFunction,
            }, cancellationToken).ConfigureAwait(false);
            if (!result.IsSucceeded)
                logger.LogError(result.Exception, "TickerQ rejected an immediate source sync drain.");
            else
                logger.LogInformation("TickerQ accepted an immediate source sync drain.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Could not schedule an immediate source sync drain.");
        }
    }
}