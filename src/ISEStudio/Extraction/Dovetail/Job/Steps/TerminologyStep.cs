using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunTerminologyAsync"/> (deterministic
/// SKOS sync + scoped terminology agent, Slice 4 sub-DAG). The
/// <c>totalProcessed</c> argument folds from
/// <see cref="JobState.ProcessedChunks"/>.
///
/// <para>Task 3 placeholder: <c>KsContext</c> / <see cref="ExtractionRequest"/>
/// are not derivable from <see cref="JobState"/> alone and are supplied as
/// <c>default!</c> until Task 4 wires the per-job closure through the Job
/// pipeline router.</para>
/// </summary>
public sealed class TerminologyStep : IPipelineSegment<JobState, TerminologyCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public TerminologyStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<TerminologyCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // Task 4: ksContext + request come from the per-job closure.
        var state = await _orchestrator
            .RunTerminologyAsync(input, default!, default!, input.ProcessedChunks, cancellationToken)
            .ConfigureAwait(false);
        return new TerminologyCarry(state);
    }
}
