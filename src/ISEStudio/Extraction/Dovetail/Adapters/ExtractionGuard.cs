using Dovetail;
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Default <see cref="IRunWithExtractionGuard"/> implementation. Wraps the
/// supplied <see cref="Func{TResult}"/> work in a "if there is already an
/// active extraction job" check (delegated to
/// <see cref="ExtractionJobStore.FindAnyActiveJobAsync"/>). When an active
/// job exists, returns the supplied conflict envelope instead of running
/// the work. The guard does NOT inspect or short-circuit cancellation
/// tokens — those propagate to the inner work as usual.
/// </summary>
public sealed class ExtractionGuard(ExtractionJobStore jobStore) : IRunWithExtractionGuard
{
    private readonly ExtractionJobStore _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));

    public async Task<T> RunAsync<T>(Func<Task<T>> work, Func<T> conflictEnvelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(conflictEnvelope);

        var active = await _jobStore.FindAnyActiveJobAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return conflictEnvelope();
        }
        return await work().ConfigureAwait(false);
    }
}
