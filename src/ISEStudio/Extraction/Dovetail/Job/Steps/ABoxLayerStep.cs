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
/// <para>Task 3 placeholder: the body is an identity fold until Task 4
/// wires the 11-argument <c>RunLayerAsync</c> call through the Job pipeline
/// router's per-job closure.</para>
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

    public Task<ABoxLayerCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // Task 4: forwards to _orchestrator.RunLayerAsync(input, chunks,
        // capacityKey, graphIri, ExtractionPhase.ABox,
        // BaseProcessedOffset(input), extractor, merger, recordMergeAsync,
        // onChunk, cancellationToken).
        _ = _orchestrator;
        _ = cancellationToken;
        return Task.FromResult(new ABoxLayerCarry(input));
    }
}
