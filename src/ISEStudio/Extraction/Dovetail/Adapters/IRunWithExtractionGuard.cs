namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Wraps the existing static <c>RunWithExtractionGuardAsync</c> as an
/// injectable abstraction so <see cref="GuardedSegment{TIn,TOut}"/> can be
/// unit-tested without a real <c>IExtractionJobStore</c>.
/// </summary>
public interface IRunWithExtractionGuard
{
    Task<T> RunAsync<T>(Func<Task<T>> work, Func<T> conflictEnvelope, CancellationToken ct);
}
