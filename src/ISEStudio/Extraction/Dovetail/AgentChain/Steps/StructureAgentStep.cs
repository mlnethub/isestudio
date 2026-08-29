using Dovetail;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: runs <see cref="IStructureAgent.AttachIsolatedAsync"/>
/// to attach isolated classes to broader parents. Fail-soft on null agent.
/// <c>maxSameParent</c> is read internally by the agent from
/// <c>ISEStudioOptions.StructureMaxSameParent</c> — not a step ctor param.
/// </summary>
public sealed class StructureAgentStep : IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult>
{
    private readonly IStructureAgent? _agent;
    private readonly ILogger<StructureAgentStep> _logger;

    public StructureAgentStep(IStructureAgent? agent, ILogger<StructureAgentStep> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task<StructureAttachResult> ExecuteAsync(
        AgentChainInput input,
        ConflictTriageResult triage,
        CancellationToken cancellationToken)
    {
        if (_agent is null)
        {
            _logger.LogWarning("StructureAgentStep: agent is null, returning empty attach log");
            return new StructureAttachResult(Array.Empty<string>());
        }

        var log = await _agent.AttachIsolatedAsync(
            input.KnowledgeSystemId,
            input.Model,
            cancellationToken,
            skipActiveExtractionGate: true).ConfigureAwait(false);

        return new StructureAttachResult(log);
    }
}