using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail adapter wrapping any Job phase segment with the per-phase
/// try/catch the pre-Dovetail orchestrator applied around every phase. On a
/// non-cancellation exception the input state is folded with
/// <see cref="JobState.Error"/> set — which flips
/// <see cref="JobState.ShouldSkipRemaining"/> so downstream phases pass the
/// state through untouched and <c>JobResult</c> reports the failure — and
/// projected onto the phase's own carry via <c>onError</c>.
///
/// <para><see cref="OperationCanceledException"/> is rethrown unchanged
/// (Dovetail README §Exception Handling) so a cancelled job stays cancelled
/// rather than being recorded as a phase failure. This is the difference
/// from <c>FailSoftSegment&lt;TIn, TOut&gt;</c>, which only rethrows when
/// the segment's own token was the one cancelled.</para>
///
/// <para>Generic on the phase carry: each Job phase has its own result type
/// (DOVE017), and a generic segment is registered by concrete type only, so
/// wrapping every phase with this adapter cannot create a shape
/// collision.</para>
/// </summary>
public sealed class PerPhaseCatchStep<TOut> : IPipelineSegment<JobState, TOut>
{
    private readonly IPipelineSegment<JobState, TOut> _inner;
    private readonly Func<JobState, TOut> _onError;

    public PerPhaseCatchStep(IPipelineSegment<JobState, TOut> inner, Func<JobState, TOut> onError)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public async Task<TOut> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return _onError(input with { Error = ex.Message });
        }
    }
}
