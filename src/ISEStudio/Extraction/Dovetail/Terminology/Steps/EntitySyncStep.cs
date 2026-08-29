using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 2 (entity sync). Runs the
/// Python decision tree per entity over the carry built by
/// <see cref="StaleMappingStep"/>. A thrown exception (cancellation aside)
/// becomes an <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class EntitySyncStep : IPipelineSegment<TerminologyInput, TermSyncCarry, EntitySyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<EntitySyncStep> _logger;

    public EntitySyncStep(TerminologyService terminology, ILogger<EntitySyncStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<EntitySyncCarry> ExecuteAsync(
        TerminologyInput input,
        TermSyncCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                new EntitySyncCarry(
                    _terminology.PassEntitySync(input.Ks, carry, cancellationToken)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EntitySyncStep: pass 2 failed (fail-soft carry)");
            return Task.FromResult(
                new EntitySyncCarry(
                    new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true)));
        }
    }
}
