using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

public sealed class ExtractionJobDispatcher
{
    private readonly ExtractionJobStore _jobs;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IExtractionJobHandler>> _handlers;

    public ExtractionJobDispatcher(IEnumerable<IExtractionJobHandler> handlers, ExtractionJobStore jobs)
    {
        _jobs = jobs;
        _handlers = handlers
            .GroupBy(item => item.Kind, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IExtractionJobHandler>)group.ToArray(),
                StringComparer.Ordinal);
    }

    public async Task DispatchAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            if (!_handlers.TryGetValue(job.Kind, out var handlers))
            {
                await _jobs.MarkFailedAsync(
                    job.Id,
                    $"Unsupported extraction kind '{job.Kind}'.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (handlers.Count != 1)
            {
                await _jobs.MarkFailedAsync(
                    job.Id,
                    $"Multiple extraction handlers were registered for kind '{job.Kind}'.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await handlers[0].HandleAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _jobs.MarkFailedAsync(
                job.Id,
                $"{exception.GetType().Name}: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }
}