using Dovetail;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;

namespace ISEStudio.Extraction.Dovetail.AgentChain;

/// <summary>
/// Dovetail pipeline that runs the extraction agent chain as three typed
/// segments: ConflictAgent → StructureAgent → StatsRefresh. Constructed via
/// <see cref="Dovetail.DovetailPipelineBuilderExtensions.AddPipelines"/>;
/// the source generator emits <c>AgentChainPipeline.g.cs</c> with the
/// <see cref="ExecuteAsync"/> method and Mermaid diagram.
/// </summary>
public partial class AgentChainPipeline : IPipeline<AgentChainInput, AgentChainResult>
{
    public AgentChainPipeline(
        [Segment] ConflictAgentStep conflictAgentStep,
        [Segment] StructureAgentStep structureAgentStep,
        [Segment] StatsRefreshStep statsRefreshStep)
    {
        ConflictAgentStep = conflictAgentStep;
        StructureAgentStep = structureAgentStep;
        StatsRefreshStep = statsRefreshStep;
    }

    public ConflictAgentStep ConflictAgentStep { get; }
    public StructureAgentStep StructureAgentStep { get; }
    public StatsRefreshStep StatsRefreshStep { get; }
}
