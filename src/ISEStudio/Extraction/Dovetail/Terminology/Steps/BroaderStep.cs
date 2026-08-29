using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 4 (broader additions). Seeds
/// <c>skos:broader</c> triples from <c>rdfs:subClassOf</c> relations among
/// mapped classes. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class BroaderStep : IPipelineSegment<TerminologyInput, AliasCarry, BroaderCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<BroaderStep> _logger;

    public BroaderStep(TerminologyService terminology, ILogger<BroaderStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<BroaderCarry> ExecuteAsync(
        TerminologyInput input,
        AliasCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                new BroaderCarry(
                    _terminology.PassBroaderAdditions(input.Ks, carry.Carry, cancellationToken)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BroaderStep: pass 4 failed (fail-soft carry)");
            return Task.FromResult(
                new BroaderCarry(
                    new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true)));
        }
    }
}
