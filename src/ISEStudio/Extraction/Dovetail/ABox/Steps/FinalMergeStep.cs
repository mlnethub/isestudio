using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Final step of ABoxJobPipeline: bundle MergeApplyOutput + CascadeResult
/// into the ABoxJobResult. Pure function, no LLM, no service dependencies.
/// Multi-input form (DOVE006 forbids bundle records).
/// </summary>
public sealed class FinalMergeStep
    : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult, ABoxJobResult>
{
    public Task<ABoxJobResult> ExecuteAsync(
        ABoxJobInput input,
        MergeApplyOutput mergeOutput,
        CascadeResult cascade,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ABoxJobResult(
            Applied: mergeOutput.Applied,
            Remaining: mergeOutput.Remaining,
            Cascade: cascade));
}
