using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunAgentChainAsync"/> (conflict triage +
/// structure attach + stats refresh, Slice 3 sub-DAG).
///
/// <para>Task 3 placeholder: the <see cref="ExtractionRequest"/> the runner
/// needs is not derivable from <see cref="JobState"/> alone and is supplied
/// as <c>default!</c> until Task 4 wires the per-job closure through the Job
/// pipeline router. The runner short-circuits to the input state when the
/// scope factory is absent, so the placeholder never dereferences the null
/// request.</para>
/// </summary>
public sealed class AgentStep : IPipelineSegment<JobState, AgentCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public AgentStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<AgentCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // Task 4: request comes from the per-job closure the router builds.
        var state = await _orchestrator
            .RunAgentChainAsync(input, default!, cancellationToken)
            .ConfigureAwait(false);
        return new AgentCarry(state);
    }
}
