using Dovetail;
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 1 of TBoxChunkPipeline: invoke the boundary critic and return the
/// filtered delta plus the critic's rejected classes. Equivalent to step 1
/// of <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class CriticStep(TBoxVerifyService verify) : IPipelineSegment<TBoxChunkInput, CriticOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<CriticOutput> ExecuteAsync(TBoxChunkInput input, CancellationToken cancellationToken)
    {
        var result = await _verify.RunCriticAsync(input.Chat, input.Text, input.Delta, cancellationToken)
            .ConfigureAwait(false);

        var acceptedNorms = result.Delta.Classes
            .Select(c => TBoxVerifyService.LabelNorm(c.Label))
            .ToHashSet(StringComparer.Ordinal);

        return new CriticOutput(
            VerifiedDelta: result.Delta,
            AcceptedNorms: acceptedNorms,
            CriticRejections: result.Rejections,
            CriticState: result);
    }
}
