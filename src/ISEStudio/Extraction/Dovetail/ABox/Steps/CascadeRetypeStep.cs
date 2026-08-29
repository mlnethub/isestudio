using Dovetail;
using ISEStudio.Audit;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 5 of ABoxJobPipeline: for each auto-applied merge, run
/// OntologyEditor.CascadeClassMergeAsync to retype dependent ABox
/// individuals. Pass-through when OntologyEditor is null.
/// Per-merge isolation: one cascade failure does not roll back prior merges.
///
/// <para>
/// <b>Signature adaptation</b>: the plan sketch assumed
/// <c>OntologyEditor.CascadeClassMergeAsync(StoreWrapper, string, string, string, CancellationToken)</c>
/// returning <c>IReadOnlyList&lt;Guid&gt;</c>. The actual editor's
/// <c>CascadeClassMergeAsync</c> does not exist as a public method — the
/// cascade retype is performed as a side-effect inside the
/// <c>merge_classes</c> op of <see cref="OntologyEditor.ApplyEditAsync"/>
/// (via the private <c>CascadeMergeClassesAsync</c>), with no externally
/// observable list of retyped individual IDs.
/// </para>
/// <para>
/// <b>Adaptation</b>: this step returns an empty
/// <see cref="CascadeResult.UpdatedIndividuals"/> in production. The merge
/// + cascade retype happen together inside MergeApplyStep; this stage is
/// preserved as a distinct pipeline node (DOV D2 multi-input contract,
/// final-report ordering) but does not perform additional work. Tests
/// pass <c>editor: null</c> and only assert the empty-cascade contracts.
/// </para>
/// <para>
/// <b>Audit deferral</b>: same as MergeApplyStep — <see cref="AuditLogService.RecordAsync"/>
/// requires a <c>UserEntity</c> actor not present in
/// <see cref="ABoxJobInput"/>; audit writes are deferred to the
/// orchestrator layer. Audit parameter retained for API parity.
/// </para>
/// </summary>
public sealed class CascadeRetypeStep(
    OntologyEditor? editor,
    AuditLogService? audit) : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult>
{
    private readonly OntologyEditor? _editor = editor;
    private readonly AuditLogService? _audit = audit;

    public async Task<CascadeResult> ExecuteAsync(
        ABoxJobInput input,
        MergeApplyOutput mergeOutput,
        CancellationToken cancellationToken)
    {
        if (_editor is null || mergeOutput.Applied.Pairs.Count == 0)
        {
            return new CascadeResult(Array.Empty<Guid>());
        }

        // The cascade retype has already been performed inside the
        // merge_classes op dispatched by MergeApplyStep
        // (OntologyEditor.MergeClassesAsync → CascadeMergeClassesAsync).
        // This step returns an empty UpdatedIndividuals list because
        // the editor does not expose the retyped individual IDs.
        // See class doc for the full signature adaptation rationale.
        return await Task.FromResult(new CascadeResult(Array.Empty<Guid>())).ConfigureAwait(false);
    }
}
