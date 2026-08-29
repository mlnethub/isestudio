using System.Reflection;
using ISEStudio.Extraction.Dovetail.AgentChain;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class AgentChainPipelineTests
{
    [Fact]
    public void AgentChainPipeline_DovetailEmitsExecuteAsync()
    {
        // Dovetail source-gen emits ExecuteAsync + Mermaid flowchart on the
        // partial class when it implements IPipeline<TIn, TOut> with
        // [Segment]-annotated ctor params. Verify the emit by reflection —
        // a DI resolve test would also work but requires the 3 step
        // interface deps (IConflictAgent / IStructureAgent /
        // IKnowledgeStatsService) to be registered, which is Task 4's
        // responsibility.
        var executeAsync = typeof(AgentChainPipeline).GetMethod(
            "ExecuteAsync",
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(AgentChainInput), typeof(CancellationToken) });

        Assert.NotNull(executeAsync);
        Assert.Equal(typeof(Task<AgentChainResult>), executeAsync!.ReturnType);

        // The 3 [Segment] properties must be exposed for Dovetail source-gen
        // to wire ExecuteAsync. Verify they exist on the partial class.
        var conflictProp = typeof(AgentChainPipeline).GetProperty(
            "ConflictAgentStep", BindingFlags.Public | BindingFlags.Instance);
        var structureProp = typeof(AgentChainPipeline).GetProperty(
            "StructureAgentStep", BindingFlags.Public | BindingFlags.Instance);
        var statsProp = typeof(AgentChainPipeline).GetProperty(
            "StatsRefreshStep", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(conflictProp);
        Assert.NotNull(structureProp);
        Assert.NotNull(statsProp);
    }
}
