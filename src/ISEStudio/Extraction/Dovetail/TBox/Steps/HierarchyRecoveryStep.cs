using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Job-level hierarchy recovery: ask the model for explicit subclass
/// edges and intermediate classes, then re-run
/// <c>TBoxVerifyService.VerifyAsync</c> for the proposed classes (per
/// spec §6 D7(a) — direct service call, not pipeline-as-segment).
/// The <see cref="HierarchyRecoveryService"/> parameter is nullable so DI
/// resolution succeeds whether or not the service is registered (Task 8
/// wires this step behind an <c>OptionalSegment</c>).
/// </summary>
public sealed class HierarchyRecoveryStep(HierarchyRecoveryService? service)
    : IPipelineSegment<TBoxJobInput, HierarchyRecoverySegmentOutput>
{
    private readonly HierarchyRecoveryService? _service = service;

    public async Task<HierarchyRecoverySegmentOutput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return new HierarchyRecoverySegmentOutput(HierarchyRecoveryResult.Empty, Enabled: false);
        }

        // Aggregate chunk delta strings for the recovery prompt context.
        var aggregatedText = string.Join("\n\n",
            input.ChunkResults.Select(r => r.Delta.ToString()).Where(s => s.Length > 0));

        var result = await _service.RecoverAsync(
            input.Chat, aggregatedText, input.FinalClassVocabulary, cancellationToken)
            .ConfigureAwait(false);

        return new HierarchyRecoverySegmentOutput(result, Enabled: true);
    }
}
