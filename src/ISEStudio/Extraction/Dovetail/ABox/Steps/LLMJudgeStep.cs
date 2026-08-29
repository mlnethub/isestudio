using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 3 of ABoxJobPipeline: LLM-judge which candidate pairs are true
/// synonyms via DuplicateJudge.JudgeDuplicatesAsync. Fail-soft on judge
/// unavailability: when DuplicateJudge is null OR the LLM call fails,
/// all candidates are kept with reason "judge_unavailable" so the cosine
/// + jaccard layers act as fallback filters.
/// </summary>
public sealed class LLMJudgeStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<JudgeResult> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Pairs.Count == 0)
        {
            return new JudgeResult(Array.Empty<int>(), Reason: null);
        }

        if (_judge is null)
        {
            var allIndices = Enumerable.Range(0, candidates.Pairs.Count).ToList();
            return new JudgeResult(allIndices, Reason: "judge_unavailable");
        }

        var pairLabels = candidates.Pairs
            .Select(p => (LabelFromIri(p.IriA), LabelFromIri(p.IriB)))
            .ToList();

        try
        {
            // DuplicateJudge.JudgeDuplicatesAsync returns HashSet<int>; the
            // brief sketch assumed IReadOnlyList<int>, so we materialise
            // to a List here to match the JudgeResult.KeptIndices contract.
            var kept = await _judge.JudgeDuplicatesAsync(pairLabels, cancellationToken)
                .ConfigureAwait(false);
            return new JudgeResult(kept.ToList(), Reason: null);
        }
        catch (Exception)
        {
            var allIndices = Enumerable.Range(0, candidates.Pairs.Count).ToList();
            return new JudgeResult(allIndices, Reason: "judge_unavailable");
        }
    }

    private static string LabelFromIri(string iri) => iri;
}
