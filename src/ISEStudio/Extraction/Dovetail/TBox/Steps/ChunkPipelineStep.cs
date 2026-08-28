using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Pipeline-as-segment adapter: pass-through for Slice 1. The actual
/// per-chunk pipeline invocation is wired into
/// <c>ExtractionOrchestrator.RunLayerAsync(TBox)</c> in Task 9, not here. A
/// future slice can fold the per-chunk pipeline invocation back into this
/// step; see spec §6 D7(a) and ledger row 20 for the design rationale.
/// </summary>
public sealed class ChunkPipelineStep : IPipelineSegment<TBoxJobInput, TBoxJobInput>
{
    public Task<TBoxJobInput> ExecuteAsync(TBoxJobInput input, CancellationToken cancellationToken) =>
        Task.FromResult(input);
}
