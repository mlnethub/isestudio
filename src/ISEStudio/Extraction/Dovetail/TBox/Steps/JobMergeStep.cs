using Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Final step of TBoxJobPipeline: bundle per-chunk results (read from
/// <see cref="TBoxJobInput.ChunkResults"/>) + corpus + hierarchy into a
/// <see cref="TBoxJobResult"/>. Pure function. Multi-input form so
/// Dovetail can wire it directly off the three prior step outputs
/// (DOVE006 forbids bundle inputs).
/// </summary>
public sealed class JobMergeStep
    : IPipelineSegment<TBoxJobInput, CorpusRecoverySegmentOutput, HierarchyRecoverySegmentOutput, TBoxJobResult>
{
    public Task<TBoxJobResult> ExecuteAsync(
        TBoxJobInput job,
        CorpusRecoverySegmentOutput corpus,
        HierarchyRecoverySegmentOutput hierarchy,
        CancellationToken cancellationToken) =>
        Task.FromResult(new TBoxJobResult(
            ChunkResults: job.ChunkResults,
            Corpus: corpus.Result,
            Hierarchy: hierarchy.Result));
}
