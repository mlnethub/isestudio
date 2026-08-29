using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunTerminologyAsync"/> (deterministic
/// SKOS sync + scoped terminology agent, Slice 4 sub-DAG). The
/// <c>totalProcessed</c> argument folds from
/// <see cref="JobState.ProcessedChunks"/>.
///
/// <para>Slice 5 Task 4 R12: forwards <see cref="JobState.KsContext"/> and
/// <see cref="JobState.Request"/> from the per-job closure the router
/// built.</para>
/// </summary>
public sealed class TerminologyStep : IPipelineSegment<JobState, TerminologyCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public TerminologyStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<TerminologyCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        var state = await _orchestrator
            .RunTerminologyAsync(
                input, input.KsContext, input.Request, input.ProcessedChunks, cancellationToken)
            .ConfigureAwait(false);
        return new TerminologyCarry(state);
    }
}
