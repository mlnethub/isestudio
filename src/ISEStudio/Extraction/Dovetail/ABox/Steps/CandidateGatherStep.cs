using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 1 of ABoxJobPipeline: read class labels from the graph and
/// produce the candidate pair set via Jaccard string similarity at or
/// above the Python DUP_THRESHOLD = 0.86 floor
/// (<see cref="DuplicateJudge.StringCandidates"/>).
/// <para>
/// When DuplicateJudge is null (DI did not register the optional service),
/// this step returns an empty CandidateList — same fail-soft contract as
/// the other ABox steps.
/// </para>
/// </summary>
public sealed class CandidateGatherStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<CandidateList> ExecuteAsync(ABoxJobInput input, CancellationToken cancellationToken)
    {
        if (_judge is null)
        {
            return new CandidateList(Array.Empty<CandidatePair>());
        }

        var labels = ConflictDetection.ReadClassLabels(input.Store, input.GraphIri);
        var pairs = DuplicateJudge.StringCandidates(labels);
        return await Task.FromResult(new CandidateList(
            pairs.Select(p => new CandidatePair(p.IriA, p.IriB, Cosine: null)).ToList())).ConfigureAwait(false);
    }
}
