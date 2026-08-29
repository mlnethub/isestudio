using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunCorpusRecoveryAsync"/> (job-level
/// second pass over the per-chunk rejection list). Short-circuits when an
/// upstream phase already failed — the spec §5.1 runtime skip condition,
/// equivalent to the pre-Dovetail control flow.
///
/// <para>Task 3 placeholder: <c>KsContext</c> and the per-chunk verify
/// outcomes are not derivable from <see cref="JobState"/> alone and are
/// supplied as <c>default!</c> until Task 4 wires the per-job closure
/// through the Job pipeline router.</para>
/// </summary>
public sealed class CorpusStep : IPipelineSegment<JobState, CorpusCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public CorpusStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<CorpusCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        if (input.ShouldSkipRemaining) return new CorpusCarry(input);

        // Task 4: ksContext + perChunk come from the per-job closure.
        var state = await _orchestrator
            .RunCorpusRecoveryAsync(input, default!, default!, cancellationToken)
            .ConfigureAwait(false);
        return new CorpusCarry(state);
    }
}
