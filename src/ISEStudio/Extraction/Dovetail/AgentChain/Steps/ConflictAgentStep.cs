using Dovetail;
using ISEStudio.Conflicts;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: runs <see cref="IConflictAgent.TriageAsync"/>
/// over the knowledge system identified in the input. Fail-soft on null
/// agent (returns empty log so downstream segments can still complete).
/// </summary>
public sealed class ConflictAgentStep : IPipelineSegment<AgentChainInput, ConflictTriageResult>
{
    private readonly IConflictAgent? _agent;
    private readonly ILogger<ConflictAgentStep> _logger;

    public ConflictAgentStep(IConflictAgent? agent, ILogger<ConflictAgentStep> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task<ConflictTriageResult> ExecuteAsync(AgentChainInput input, CancellationToken cancellationToken)
    {
        if (_agent is null)
        {
            _logger.LogWarning("ConflictAgentStep: agent is null, returning empty triage log");
            return new ConflictTriageResult(Array.Empty<string>());
        }

        var log = await _agent.TriageAsync(
            input.KnowledgeSystemId,
            cancellationToken,
            input.Model,
            skipActiveExtractionGate: true).ConfigureAwait(false);

        return new ConflictTriageResult(log);
    }
}