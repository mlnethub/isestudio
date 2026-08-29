using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Identity segment for the agent-chain slot: folds <see cref="JobState"/>
/// straight into an <see cref="AgentCarry"/> without running the Slice 3
/// agent sub-DAG. Registered in place of <see cref="AgentStep"/> when the
/// per-job service scope factory is not wired (hand-built orchestrators,
/// agent chain disabled by options) so the Job DAG keeps a constant shape
/// while the phase becomes a no-op.
///
/// <para>Factory rather than a segment class of its own: a second concrete
/// <c>IPipelineSegment&lt;JobState, AgentCarry&gt;</c> implementation would
/// collide with <see cref="AgentStep"/> under DOVE017. The generic
/// <see cref="NoOpSegment{TIn, TOut}"/> adapter is registered by concrete
/// type only, so it is exempt — the same reason the Slice 1-4 pipelines
/// fall back to it.</para>
/// </summary>
public static class NoOpAgentStep
{
    /// <summary>Build the identity agent segment.</summary>
    public static IPipelineSegment<JobState, AgentCarry> Create() =>
        new NoOpSegment<JobState, AgentCarry>(static state => new AgentCarry(state));
}
