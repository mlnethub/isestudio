using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology init + pass 1 (stale mappings).
/// The init half builds the pass-shared carry
/// (<see cref="TerminologyService.PrepareCarry"/>); the pass half prunes
/// <c>op:mapsTo</c> triples whose target no longer exists in the ontology
/// or ABox. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class StaleMappingStep : IPipelineSegment<TerminologyInput, TermSyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<StaleMappingStep> _logger;

    public StaleMappingStep(TerminologyService terminology, ILogger<StaleMappingStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<TermSyncCarry> ExecuteAsync(TerminologyInput input, CancellationToken cancellationToken)
    {
        try
        {
            var carry0 = _terminology.PrepareCarry(input.Ks, cancellationToken);
            return Task.FromResult(
                _terminology.PassStaleMappings(input.Ks, carry0, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StaleMappingStep: init/pass 1 failed (fail-soft carry)");
            return Task.FromResult(
                new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true));
        }
    }
}
