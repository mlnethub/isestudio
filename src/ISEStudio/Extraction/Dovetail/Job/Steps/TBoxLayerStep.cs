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
/// <para>Task 3 placeholder: <c>RunLayerAsync</c> takes 11 arguments (chunk
/// spans, capacity key, graph IRI, extractor / merger / merge-record /
/// on-chunk delegates) that only the per-job closure can produce. The body
/// is an identity fold until Task 4 wires those delegates through the Job
/// pipeline router.</para>
/// </summary>
public sealed class TBoxLayerStep : IPipelineSegment<JobState, TBoxLayerCarry>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public TBoxLayerStep(ExtractionOrchestrator orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public Task<TBoxLayerCarry> ExecuteAsync(JobState input, CancellationToken cancellationToken)
    {
        // Task 4: forwards to _orchestrator.RunLayerAsync(input, chunks,
        // capacityKey, graphIri, ExtractionPhase.TBox, baseProcessedOffset: 0,
        // extractor, merger, recordMergeAsync, onChunk, cancellationToken).
        _ = _orchestrator;
        _ = cancellationToken;
        return Task.FromResult(new TBoxLayerCarry(input));
    }
}
