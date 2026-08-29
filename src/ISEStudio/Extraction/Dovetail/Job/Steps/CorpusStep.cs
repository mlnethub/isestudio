using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunCorpusRecoveryAsync"/> (job-level
/// second pass over the per-chunk rejection list). Short-circuits when an
/// upstream phase already failed — the spec §5.1 runtime skip condition,
/// equivalent to the pre-Dovetail control flow.
///
/// <para>Slice 5 Task 4 R12: forwards <see cref="JobState.KsContext"/> and
/// <see cref="JobState.PerChunk"/> from the per-job closure the router
/// built.</para>
/// </summary>
public sealed class CorpusStep : IPipelineSegment<JobState, CorpusCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public CorpusStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<CorpusCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        if (input.ShouldSkipRemaining) return new CorpusCarry(input);

        var state = await _orchestrator
            .RunCorpusRecoveryAsync(input, input.KsContext, input.PerChunk, cancellationToken)
            .ConfigureAwait(false);
        return new CorpusCarry(state);
    }
}
