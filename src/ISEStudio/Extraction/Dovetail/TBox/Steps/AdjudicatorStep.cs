using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 2 of TBoxChunkPipeline. Catches its own adjudicator exception and
/// falls back to denotation over the ORIGINAL chunk delta (not the
/// critic-filtered subset), matching the fail-soft branch of
/// <c>TBoxVerifyService.VerifyAsync</c>. The outer <c>FailSoftSegment</c>
/// in the pipeline ctor stays as a defense-in-depth wrapper but never
/// triggers because this step does not throw.
/// </summary>
public sealed class AdjudicatorStep(TBoxVerifyService verify) : IPipelineSegment<AdjudicatorInput, AdjudicatorOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<AdjudicatorOutput> ExecuteAsync(AdjudicatorInput input, CancellationToken cancellationToken)
    {
        var disputed = input.Chunk.Delta.Classes
            .Where(c => !input.Critic.AcceptedNorms.Contains(TBoxVerifyService.LabelNorm(c.Label)))
            .ToList();

        if (disputed.Count == 0)
        {
            return new AdjudicatorOutput(
                Succeeded: true,
                Recovered: Array.Empty<ClassMutation>(),
                DenotationFallback: null);
        }

        var firstReasons = input.Critic.CriticRejections.ToDictionary(
            r => TBoxVerifyService.LabelNorm(r.Label), r => r.Reason, StringComparer.Ordinal);

        try
        {
            var result = await _verify.RunAdjudicatorAsync(
                input.Chunk.Chat, input.Chunk.Text, disputed, firstReasons,
                new Dictionary<string, double>(), input.Critic.CriticState,
                cancellationToken).ConfigureAwait(false);

            return new AdjudicatorOutput(
                Succeeded: true,
                Recovered: result.Delta.Classes,
                DenotationFallback: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fail-soft: adjudicator failed. Re-run denotation over the
            // ORIGINAL chunk delta (delta.Classes), not the critic-filtered
            // subset — this matches the original VerifyAsync catch-block
            // behavior and is required by VerifyAsync_adjudicator_failure_is_fail_soft.
            var fallback = await _verify.RunDenotationAsync(
                input.Chunk.Chat, input.Chunk.Text,
                input.Chunk.Delta.Classes,
                new HashSet<string>(input.Critic.AcceptedNorms, StringComparer.Ordinal),
                input.Critic.CriticState with { Rejections = Array.Empty<RejectedClass>() },
                cancellationToken).ConfigureAwait(false);

            return new AdjudicatorOutput(
                Succeeded: false,
                Recovered: Array.Empty<ClassMutation>(),
                DenotationFallback: fallback);
        }
    }
}

/// <summary>Bundle of chunk + critic output for AdjudicatorStep.</summary>
public sealed record AdjudicatorInput(TBoxChunkInput Chunk, CriticOutput Critic);
