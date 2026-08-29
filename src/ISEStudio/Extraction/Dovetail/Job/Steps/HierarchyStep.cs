using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunHierarchyRecoveryAsync"/> (per-chunk
/// super-class / edge recovery pass). Short-circuits when an upstream phase
/// already failed — the spec §5.1 runtime skip condition, equivalent to the
/// pre-Dovetail control flow.
///
/// <para>Slice 5 Task 4 R12: forwards <see cref="JobState.KsContext"/>,
/// <see cref="JobState.Request"/>, and <see cref="JobState.PerChunk"/> from
/// the per-job closure the router built.</para>
/// </summary>
public sealed class HierarchyStep : IPipelineSegment<JobState, HierarchyCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public HierarchyStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<HierarchyCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        if (input.ShouldSkipRemaining) return new HierarchyCarry(input);

        var state = await _orchestrator
            .RunHierarchyRecoveryAsync(
                input, input.KsContext, input.Request, input.PerChunk, cancellationToken)
            .ConfigureAwait(false);
        return new HierarchyCarry(state);
    }
}
