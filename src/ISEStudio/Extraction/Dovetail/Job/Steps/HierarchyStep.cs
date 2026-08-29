using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunHierarchyRecoveryAsync"/> (per-chunk
/// super-class / edge recovery pass). Short-circuits when an upstream phase
/// already failed — the spec §5.1 runtime skip condition, equivalent to the
/// pre-Dovetail control flow.
///
/// <para>Task 3 placeholder: <c>KsContext</c> / <see cref="ExtractionRequest"/>
/// and the per-chunk verify outcomes are not derivable from
/// <see cref="JobState"/> alone and are supplied as <c>default!</c> until
/// Task 4 wires the per-job closure through the Job pipeline router.</para>
/// </summary>
public sealed class HierarchyStep : IPipelineSegment<JobState, HierarchyCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public HierarchyStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<HierarchyCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        if (input.ShouldSkipRemaining) return new HierarchyCarry(input);

        // Task 4: ksContext + request + perChunk come from the per-job closure.
        var state = await _orchestrator
            .RunHierarchyRecoveryAsync(input, default!, default!, default!, cancellationToken)
            .ConfigureAwait(false);
        return new HierarchyCarry(state);
    }
}
