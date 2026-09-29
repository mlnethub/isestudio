using TickerQ.Utilities.Base;

namespace ISEStudio.Sources;

internal sealed class SourceSyncTickerFunctions(
    SourceSyncScheduler scheduler,
    SourceSyncWorker worker)
{
    public const string DrainFunction = "source-sync-drain";

    [TickerFunction(DrainFunction)]
    public async Task DrainAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        await worker.DrainQueuedAsync(cancellationToken).ConfigureAwait(false);
    }

    [TickerFunction("source-sync-recovery", cronExpression: "* * * * *")]
    public async Task ScheduleAndDrainAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        await scheduler.ScheduleDueSourcesAsync(cancellationToken).ConfigureAwait(false);
        await worker.DrainQueuedAsync(cancellationToken).ConfigureAwait(false);
    }
}