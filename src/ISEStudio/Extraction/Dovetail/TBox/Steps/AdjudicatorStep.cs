using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 2 of TBoxChunkPipeline. Catches its own adjudicator exception and
/// falls back to denotation over the ORIGINAL chunk delta (not the
/// critic-filtered subset), matching the fail-soft branch of
/// <c>TBoxVerifyService.VerifyAsync</c>. The outer <c>FailSoftSegment</c>
/// wrapper that earlier drafts proposed is unnecessary — this step already
/// never throws on adjudicator failure.
/// </summary>
public sealed class AdjudicatorStep(TBoxVerifyService verify)
    : IPipelineSegment<TBoxChunkInput, CriticOutput, AdjudicatorOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<AdjudicatorOutput> ExecuteAsync(
        TBoxChunkInput chunk,
        CriticOutput critic,
        CancellationToken cancellationToken)
    {
        var disputed = chunk.Delta.Classes
            .Where(c => !critic.AcceptedNorms.Contains(TBoxVerifyService.LabelNorm(c.Label)))
            .ToList();

        if (disputed.Count == 0)
        {
            return new AdjudicatorOutput(
                Succeeded: true,
                Recovered: Array.Empty<ClassMutation>(),
                DenotationFallback: null);
        }

        var firstReasons = critic.CriticRejections.ToDictionary(
            r => TBoxVerifyService.LabelNorm(r.Label), r => r.Reason, StringComparer.Ordinal);

        try
        {
            var result = await _verify.RunAdjudicatorAsync(
                chunk.Chat, chunk.Text, disputed, firstReasons,
                new Dictionary<string, double>(), critic.CriticState,
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
                chunk.Chat, chunk.Text,
                chunk.Delta.Classes,
                new HashSet<string>(critic.AcceptedNorms, StringComparer.Ordinal),
                critic.CriticState with { Rejections = Array.Empty<RejectedClass>() },
                cancellationToken).ConfigureAwait(false);

            return new AdjudicatorOutput(
                Succeeded: false,
                Recovered: Array.Empty<ClassMutation>(),
                DenotationFallback: fallback);
        }
    }
}