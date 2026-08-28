using Dovetail;
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 3 of TBoxChunkPipeline. Equivalent to step 3 of
/// <c>TBoxVerifyService.VerifyAsync</c>. Multi-input form so Dovetail can
/// wire it directly off the pipeline's <see cref="CriticStep"/> and
/// <see cref="AdjudicatorStep"/> outputs (DOVE006 forbids bundle inputs).
/// </summary>
public sealed class DenotationStep(TBoxVerifyService verify)
    : IPipelineSegment<TBoxChunkInput, CriticOutput, AdjudicatorOutput, DenotationOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<DenotationOutput> ExecuteAsync(
        TBoxChunkInput chunk,
        CriticOutput critic,
        AdjudicatorOutput adjudicator,
        CancellationToken cancellationToken)
    {
        var result = await _verify.RunDenotationAsync(
            chunk.Chat, chunk.Text,
            chunk.Delta.Classes,
            new HashSet<string>(critic.AcceptedNorms, StringComparer.Ordinal),
            critic.CriticState with { Rejections = Array.Empty<RejectedClass>() },
            cancellationToken).ConfigureAwait(false);

        return new DenotationOutput(
            VerifiedDelta: result.Delta,
            Rejections: result.Rejections,
            Recoveries: result.Recoveries,
            DenotationState: result);
    }
}