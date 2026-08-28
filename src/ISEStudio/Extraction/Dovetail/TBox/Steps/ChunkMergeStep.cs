using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Final step of TBoxChunkPipeline. If <see cref="AdjudicatorOutput.DenotationFallback"/>
/// is non-null, returns it directly (adjudicator failed; denotation already ran
/// inside AdjudicatorStep over the original chunk delta). Otherwise merges the
/// normal denotation output with the adjudicator's recovered classes.
/// </summary>
public sealed class ChunkMergeStep : IPipelineSegment<MergeInput, TBoxVerifyResult>
{
    public Task<TBoxVerifyResult> ExecuteAsync(MergeInput input, CancellationToken cancellationToken)
    {
        // Fail-soft path: adjudicator failed; AdjudicatorStep already ran the
        // fallback denotation over the original chunk delta.
        if (input.Adjudicator.DenotationFallback is { } fallback)
        {
            return Task.FromResult(fallback);
        }

        var denotated = input.Denotation;

        var finalClasses = new List<ClassMutation>(denotated.VerifiedDelta.Classes);
        var finalNorms = finalClasses.Select(c => TBoxVerifyService.LabelNorm(c.Label))
            .ToHashSet(StringComparer.Ordinal);
        var recoveries = new List<RecoveredClass>(denotated.Recoveries);

        if (input.Adjudicator.Succeeded)
        {
            foreach (var row in input.Adjudicator.Recovered)
            {
                var norm = TBoxVerifyService.LabelNorm(row.Label);
                if (norm.Length == 0 || finalNorms.Contains(norm)) continue;
                finalNorms.Add(norm);
                finalClasses.Add(row);
                recoveries.Add(new RecoveredClass(row.Label));
            }
        }

        var rejections = new List<RejectedClass>(denotated.Rejections);

        var merged = denotated.DenotationState with
        {
            Delta = denotated.VerifiedDelta with { Classes = finalClasses },
            Rejections = rejections,
            Recoveries = recoveries,
        };
        return Task.FromResult(merged);
    }
}

/// <summary>All three step outputs + chunk input bundled for ChunkMergeStep.</summary>
public sealed record MergeInput(
    TBoxChunkInput Chunk,
    CriticOutput Critic,
    AdjudicatorOutput Adjudicator,
    DenotationOutput Denotation);
