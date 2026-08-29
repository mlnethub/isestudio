using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: P3-1 terminology proposals. The gating
/// (<c>SuggestEnabled</c> / carry error / SchemeIri) runs inside the step
/// (spec §5 D6); the chunk-id query mirrors
/// <c>ExtractionOrchestrator.RunTerminologyAgentAsync</c>; the accepted-row
/// count folds into the final <see cref="TerminologyResult"/> via
/// <see cref="TerminologyService.FoldCarry"/>. Agent exceptions propagate
/// (P1-4 parity — the orchestrator's outer catch marks the capture), and a
/// null agent (hand-built step tests) folds fail-soft.
/// </summary>
public sealed class ProposalStep : IPipelineSegment<TerminologyInput, BroaderCarry, TerminologyResult>
{
    private readonly TerminologyAgent? _agent;
    private readonly ISEStudioDbContext _db;
    private readonly ILogger<ProposalStep> _logger;
    private readonly int _maxChunks;

    public ProposalStep(
        TerminologyAgent? agent,
        ISEStudioDbContext db,
        IOptions<ISEStudioOptions> options,
        ILogger<ProposalStep> logger)
    {
        _agent = agent;
        _db = db;
        _logger = logger;
        _maxChunks = options.Value.TerminologySuggestionMaxChunks;
    }

    public async Task<TerminologyResult> ExecuteAsync(
        TerminologyInput input,
        BroaderCarry broaderCarry,
        CancellationToken cancellationToken)
    {
        var carry = broaderCarry.Carry;
        var folded = TerminologyService.FoldCarry(carry);

        if (!input.SuggestEnabled || carry.Error is not null || string.IsNullOrEmpty(carry.SchemeIri))
        {
            return folded;
        }

        if (_agent is null)
        {
            _logger.LogWarning("ProposalStep: agent is null, folding carry without proposals");
            return folded;
        }

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == input.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
        {
            return folded;
        }

        // job.ChunkIds stores ChunkSpan.Idx (an in-memory 0-based index,
        // not ChunkEntity.Id), so we cannot feed it to the agent directly.
        // Query the parsed-document chunks belonging to this knowledge
        // system, ordered for stable propose prompts (Python
        // _terminology_rows orders by document then chunk order too).
        // ChunkEntity has no `Document` navigation property — the join is
        // explicit, mirroring TerminologyAgent.LoadChunksAsync. Phase 3:
        // legacy_id 列已退役; we hand the agent Guid PKs.
        var chunkIds = await _db.Chunks.AsNoTracking()
            .Join(_db.Documents,
                c => c.DocumentId,
                d => d.Id,
                (c, d) => new { Chunk = c, Document = d })
            .Where(join => join.Document.KnowledgeSystemId == ks.Id
                && join.Document.ParseStatus == "parsed")
            .OrderBy(join => join.Chunk.DocumentId).ThenBy(join => join.Chunk.Idx)
            .Take(_maxChunks)
            .Select(join => join.Chunk.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (chunkIds.Count == 0)
        {
            return folded;
        }

        var proposals = await _agent.SuggestAsync(
            ks, carry.SchemeIri!, chunkIds, input.Model, cancellationToken)
            .ConfigureAwait(false);
        return folded with { ProposalsQueued = proposals.Count };
    }
}
