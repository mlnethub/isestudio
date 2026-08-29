using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail Job segment for the ABox chunk layer. Wraps
/// <see cref="ExtractionOrchestrator.RunLayerAsync"/> with
/// <c>phase = ExtractionPhase.ABox</c> and a <c>baseProcessedOffset</c> of
/// <see cref="JobState.ChunkIds"/>.Count for <see cref="JobKind.Combined"/>
/// (the ABox layer resumes the progress counter after the TBox layer) or
/// <c>0</c> for <see cref="JobKind.ABoxOnly"/>.
///
/// <para>Slice 5 Task 3 ruling R3: non-generic sibling of
/// <see cref="TBoxLayerStep"/> — see that type for why the plan's generic
/// <c>LayerStep&lt;TPipeline&gt;</c> was split.</para>
///
/// <para>Slice 5 Task 4 R12: forwards to
/// <see cref="ExtractionOrchestrator.RunLayerAsync"/> with the
/// ABox-specific extractor (<c>_abox.ExtractAsync</c>), merger
/// (<c>MergeABox</c>) and merge-record (<c>RecordABoxMergeAsync</c>).
/// ABox does not capture a perChunk list (no verify pass) and the
/// <c>onChunk</c> callback is <c>null</c>, matching the legacy
/// <c>ABoxOnlyRunnerAsync</c>.</para>
/// </summary>
public sealed class ABoxLayerStep : IPipelineSegment<JobState, ABoxLayerCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public ABoxLayerStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    /// <summary>
    /// Progress-counter base for the ABox layer: a combined job reports a
    /// single <c>total_chunks = 2 * N</c> counter, so the ABox layer starts
    /// where the TBox layer stopped.
    /// </summary>
    internal static int BaseProcessedOffset(JobState state) =>
        state.Kind == JobKind.Combined ? state.ChunkIds.Count : 0;

    public async Task<ABoxLayerCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // ABox extraction needs the existing class labels so the LLM can
        // ground the extracted individuals against the schema. Pull the
        // live labels off the KsContext's TBox graph via the orchestrator's
        // forwarder (ExistingClassLabels is private).
        var labels = _orchestrator.ExistingClassLabelsForStep(input.KsContext);

        var state = await _orchestrator.RunLayerAsync(
            input,
            input.Chunks,
            capacityKey: input.Request.CapacityKey,
            graphIri: input.KsContext.ABoxGraph,
            phase: ExtractionPhase.ABox,
            baseProcessedOffset: BaseProcessedOffset(input),
            extractor: async (chunk, ct) =>
                (object)await _orchestrator.ExtractABoxForStepAsync(
                    input, input.KsContext, chunk, labels, ct).ConfigureAwait(false),
            merger: delta => _orchestrator.MergeABoxForStep(
                input.KsContext, (ABoxDelta)delta),
            recordMergeAsync: (id, result, ct) =>
                _orchestrator.RecordABoxMergeForStepAsync(id, result, ct),
            onChunk: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ABoxLayerCarry(state);
    }
}
