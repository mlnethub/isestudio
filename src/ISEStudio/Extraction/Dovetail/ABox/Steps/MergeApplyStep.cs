using Dovetail;
using ISEStudio.Audit;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 4 of ABoxJobPipeline: for each judge-kept pair, decide between
/// auto-applying the merge (high-confidence) or emitting a
/// ConflictDetection.DetectedConflict for triage (low-confidence).
/// Threshold comes from ABoxJobInput.MinConfidence, which the orchestrator
/// wires from ISEStudioOptions.DuplicateAutoApplyFloor.
///
/// Per-merge QuadChangeCapture with revertOnError:false per spec §4 D5
/// (LOCKED): one failed merge does not roll back successful ones.
///
/// <para>
/// <b>Signature adaptation</b>: the plan sketch assumed
/// <c>OntologyEditor.ApplyClassMergeAsync(StoreWrapper, string, string, string, CancellationToken)</c>
/// but the actual editor exposes a single <see cref="OntologyEditor.ApplyEditAsync"/>
/// dispatch with an <c>op</c> dictionary. The <c>merge_classes</c> op
/// internally performs both the TBox merge AND the ABox cascade-retype
/// (so CascadeRetypeStep has nothing separate to do — see its doc for
/// why it returns empty UpdatedIndividuals in production).
/// </para>
/// <para>
/// <b>Audit deferral</b>: <see cref="AuditLogService.RecordAsync"/>
/// requires a <c>UserEntity</c> actor that <see cref="ABoxJobInput"/>
/// does not carry. The audit parameter is retained so DI can wire it
/// later, but the step does NOT call it from this slice — audit writes
/// for auto-applied merges are deferred to the orchestrator / ConflictService
/// layer that owns the DbContext + actor context (matching the P3-11
/// ConflictAgent direct-write pattern). Tests pass <c>audit: null</c>.
/// </para>
/// </summary>
public sealed class MergeApplyStep(
    OntologyEditor? editor,
    AuditLogService? audit) : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult, MergeApplyOutput>
{
    private readonly OntologyEditor? _editor = editor;
    // Audit retained for API compatibility (and future wiring); currently
    // unused because AuditLogService.RecordAsync requires a UserEntity
    // actor that the pipeline input does not carry. See class doc.
    private readonly AuditLogService? _audit = audit;

    public async Task<MergeApplyOutput> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        JudgeResult judge,
        CancellationToken cancellationToken)
    {
        if (judge.KeptIndices.Count == 0)
        {
            return new MergeApplyOutput(
                Applied: new AppliedMerges(Array.Empty<MergedClassPair>()),
                Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));
        }

        var applied = new List<MergedClassPair>();
        var remaining = new List<ConflictDetection.DetectedConflict>();

        foreach (var idx in judge.KeptIndices)
        {
            if (idx < 0 || idx >= candidates.Pairs.Count) continue;
            var pair = candidates.Pairs[idx];

            var cosine = pair.Cosine ?? 0.0;
            var passesConfidence = cosine >= input.MinConfidence;

            if (passesConfidence && _editor is not null)
            {
                try
                {
                    // The merge_classes op internally performs both the
                    // TBox merge AND the ABox cascade-retype (see
                    // OntologyEditor.MergeClassesAsync + CascadeMergeClassesAsync).
                    // ApplyEditAsync's own capture handles the per-merge
                    // revertOnError:false + MarkError() pattern.
                    var op = new Dictionary<string, object?>
                    {
                        ["op"] = "merge_classes",
                        ["source"] = pair.IriA,
                        ["target"] = pair.IriB,
                    };
                    await _editor.ApplyEditAsync(input.GraphIri, baseIri: string.Empty, op, cancellationToken)
                        .ConfigureAwait(false);
                    applied.Add(new MergedClassPair(pair.IriA, pair.IriB, cosine));

                    // Audit deferred to orchestrator layer (see class doc).
                }
                catch (Exception)
                {
                    // One failed merge must not roll back prior merges
                    // (spec §4 D5 LOCKED: per-merge capture isolation).
                    // Audit deferred to orchestrator layer (see class doc).
                }
            }
            else
            {
                // Either below threshold OR editor unavailable: emit
                // conflict for triage. When the editor is null (DI did
                // not register it) every kept pair is forwarded to the
                // conflict queue so the ConflictAgent path can pick it
                // up — matching the brief's "null editor returns empty
                // applied + all remaining" contract.
                var orderedPair = new[] { pair.IriA, pair.IriB }.OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var conflict = new ConflictDetection.DetectedConflict(
                    Signature: "duplicate|" + string.Join("|", orderedPair),
                    Ctype: "duplicate",
                    Severity: "warning",
                    Title: "Possible duplicate classes (low confidence)",
                    Detail: $"\"{pair.IriA}\" and \"{pair.IriB}\" look similar but cosine {cosine:F2} below floor {input.MinConfidence:F2}.",
                    Entities: new[]
                    {
                        new ConflictDetection.EntityRef(pair.IriA, pair.IriA),
                        new ConflictDetection.EntityRef(pair.IriB, pair.IriB),
                    },
                    Resolutions: Array.Empty<ConflictDetection.Resolution>());
                remaining.Add(conflict);
            }
        }

        return new MergeApplyOutput(
            new AppliedMerges(applied),
            new RemainingConflicts(remaining));
    }
}
