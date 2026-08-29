using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment forwarding to
/// <see cref="ExtractionOrchestrator.RunAgentChainAsync"/> (conflict triage +
/// structure attach + stats refresh, Slice 3 sub-DAG).
///
/// <para>Slice 5 Task 4 R12: forwards the per-job closure's
/// <see cref="ISEStudio.Extraction.ExtractionRequest"/> from
/// <see cref="JobState.Request"/>; the runner short-circuits to the input
/// state when the scope factory is absent (hand-built test orchestrators
/// skip the chain entirely, the P1-4 seam).</para>
/// </summary>
public sealed class AgentStep : IPipelineSegment<JobState, AgentCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public AgentStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<AgentCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        var state = await _orchestrator
            .RunAgentChainAsync(input, input.Request, cancellationToken)
            .ConfigureAwait(false);
        return new AgentCarry(state);
    }
}
