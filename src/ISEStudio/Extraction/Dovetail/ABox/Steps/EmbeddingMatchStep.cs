using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 2 of ABoxJobPipeline: enrich the candidate set with embedding
/// cosine scores from DuplicateJudge.EmbeddingCandidatesAsync. Pass-through
/// when DuplicateJudge is null (semantic-conflicts disabled).
/// </summary>
public sealed class EmbeddingMatchStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList, CandidateList>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<CandidateList> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        CancellationToken cancellationToken)
    {
        if (_judge is null || candidates.Pairs.Count == 0)
        {
            return candidates;
        }

        // Reconstruct labels and call the legacy embedding service.
        var labels = ConflictDetection.ReadClassLabels(input.Store, input.GraphIri);
        var cosineResults = await _judge.EmbeddingCandidatesAsync(
            labels,
            input.MinConfidence,
            cancellationToken).ConfigureAwait(false);

        // Map results back to candidate pairs (results may be unordered,
        // and may contain both (a,b) and (b,a) orderings). DuplicateJudge's
        // EmbeddingCandidatesAsync returns tuples of ((IriA, IriB), Cosine).
        var cosineMap = new Dictionary<(string, string), double>();
        foreach (var (pair, cos) in cosineResults)
        {
            cosineMap[pair] = cos;
        }

        var merged = candidates.Pairs.Select(p =>
        {
            var key = (p.IriA, p.IriB);
            var reverse = (p.IriB, p.IriA);
            var cos = cosineMap.TryGetValue(key, out var c) ? (double?)c
                    : cosineMap.TryGetValue(reverse, out c) ? (double?)c
                    : null;
            return new CandidatePair(p.IriA, p.IriB, cos);
        }).ToList();

        return new CandidateList(merged);
    }
}
