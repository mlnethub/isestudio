using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Job-level corpus recovery: aggregate per-chunk rejections, ask the
/// selector + recovery prompts to pick passages and adjudicate. The
/// <see cref="CorpusRecoveryService"/> parameter is nullable; Task 8 wires
/// this step behind an <c>OptionalSegment</c> so DI resolution succeeds
/// whether or not the service is registered.
/// </summary>
public sealed class CorpusRecoveryStep(CorpusRecoveryService? service)
    : IPipelineSegment<TBoxJobInput, CorpusRecoverySegmentOutput>
{
    private readonly CorpusRecoveryService? _service = service;

    public async Task<CorpusRecoverySegmentOutput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return new CorpusRecoverySegmentOutput(CorpusRecoveryResult.Empty, Enabled: false);
        }

        var perChunk = input.PerChunkRejections
            .Select(r => new CorpusRecoveryChunk(r.ChunkId, r.Text, r.Rejected))
            .ToList();
        var existingNorms = input.FinalClassVocabulary
            .Select(TBoxVerifyService.LabelNorm)
            .ToHashSet(StringComparer.Ordinal);

        var result = await _service.RecoverAsync(input.Chat, perChunk, existingNorms, cancellationToken)
            .ConfigureAwait(false);

        return new CorpusRecoverySegmentOutput(result, Enabled: true);
    }
}
