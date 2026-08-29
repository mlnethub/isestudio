using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment for the TBox chunk layer. Wraps
/// <see cref="ExtractionOrchestrator.RunLayerAsync"/> with
/// <c>phase = ExtractionPhase.TBox</c> and <c>baseProcessedOffset = 0</c>
/// (the TBox layer is always the first layer of a job, whether the kind is
/// <see cref="JobKind.TBoxOnly"/> or <see cref="JobKind.Combined"/>).
///
/// <para>Slice 5 Task 3 ruling R3: the plan's generic
/// <c>LayerStep&lt;TPipeline&gt;</c> could not be used —
/// <c>ABoxJobPipeline</c> does not satisfy the
/// <c>IPipeline&lt;TBoxChunkInput, TBoxVerifyResult&gt;</c> constraint — so
/// the layer step is split into two non-generic classes, which is also the
/// design doc's documented DOVE017 fallback.</para>
///
/// <para>Slice 5 Task 4 R12: forwards to
/// <see cref="ExtractionOrchestrator.RunLayerAsync"/> with the TBox-specific
/// extractor (extract + verify), merger (<c>MergeTBox</c>), merge-record
/// (<c>RecordTBoxMergeAsync</c>) and on-chunk (capture
/// <see cref="ChunkVerifyOutcome"/> list for downstream recovery passes).
/// The perChunk list is folded back into <see cref="JobState.PerChunk"/>
/// when RunLayerAsync returns so the downstream <see cref="CorpusStep"/> /
/// <see cref="HierarchyStep"/> see the populated list.</para>
/// </summary>
public sealed class TBoxLayerStep : IPipelineSegment<JobState, TBoxLayerCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public TBoxLayerStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<TBoxLayerCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // R12: capture perChunk inside the onChunk delegate so the
        // downstream Corpus / Hierarchy recovery passes see the populated
        // list. The list is appended to via the closure the runner invokes
        // once per chunk, so it must be a List<T> (IReadOnlyList cannot
        // grow). The captured list is folded back into the returned state
        // when RunLayerAsync finishes.
        var perChunk = new List<ChunkVerifyOutcome>();
        var state = await _orchestrator.RunLayerAsync(
            input,
            input.Chunks,
            capacityKey: input.Request.CapacityKey,
            graphIri: input.KsContext.TBoxGraph,
            phase: ExtractionPhase.TBox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                (object)await _orchestrator.ExtractAndVerifyForStepAsync(
                    input, input.KsContext, chunk, ct).ConfigureAwait(false),
            merger: item => _orchestrator.MergeTBoxForStep(
                input.KsContext,
                ((VerifiedTBox)item).Delta,
                ((VerifiedTBox)item).Verify),
            recordMergeAsync: (id, result, ct) =>
                _orchestrator.RecordTBoxMergeForStepAsync(id, result, ct),
            onChunk: (i, item) =>
            {
                var verified = (VerifiedTBox)item;
                perChunk.Add(new ChunkVerifyOutcome(
                    input.Chunks[i].Idx,
                    input.Chunks[i].Text,
                    verified.Verify?.Rejections ?? Array.Empty<RejectedClass>()));
                return default;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // R12 ruling on perChunk tracking: replace JobState.PerChunk with
        // the captured list so downstream recovery steps observe the
        // populated outcome. Use AsReadOnly so the contract stays an
        // IReadOnlyList.
        if (!ReferenceEquals(state.PerChunk, perChunk))
        {
            state = state with { PerChunk = perChunk.AsReadOnly() };
        }
        return new TBoxLayerCarry(state);
    }
}
