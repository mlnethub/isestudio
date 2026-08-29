using Dovetail;
using ISEStudio.Knowledge;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: best-effort
/// <see cref="IKnowledgeStatsService.RefreshAsync"/>. Fail-soft: stats refresh
/// exceptions are swallowed and logged, never propagated (Slice 3 spec §5 D4,
/// matching P1-4 LOCKED decision).
/// </summary>
public sealed class StatsRefreshStep : IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult, AgentChainResult>
{
    private readonly IKnowledgeStatsService? _stats;
    private readonly ILogger<StatsRefreshStep> _logger;

    public StatsRefreshStep(IKnowledgeStatsService? stats, ILogger<StatsRefreshStep> logger)
    {
        _stats = stats;
        _logger = logger;
    }

    public async Task<AgentChainResult> ExecuteAsync(
        AgentChainInput input,
        ConflictTriageResult triage,
        StructureAttachResult structure,
        CancellationToken cancellationToken)
    {
        if (_stats is not null)
        {
            try
            {
                await _stats.RefreshAsync(input.KnowledgeSystemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StatsRefreshStep: stats refresh failed (fail-soft, continuing)");
            }
        }

        return new AgentChainResult(triage, structure);
    }
}