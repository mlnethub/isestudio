using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 3 (alias additions). Attaches
/// each mapped concept's entity label as an <c>skos:altLabel</c> when it is
/// not already attached. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class AliasStep : IPipelineSegment<TerminologyInput, EntitySyncCarry, AliasCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<AliasStep> _logger;

    public AliasStep(TerminologyService terminology, ILogger<AliasStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<AliasCarry> ExecuteAsync(
        TerminologyInput input,
        EntitySyncCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                new AliasCarry(
                    _terminology.PassAliasAdditions(input.Ks, carry.Carry, cancellationToken)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliasStep: pass 3 failed (fail-soft carry)");
            return Task.FromResult(
                new AliasCarry(
                    new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true)));
        }
    }
}
