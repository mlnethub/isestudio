using Dovetail;
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 3 of TBoxChunkPipeline. Equivalent to step 3 of
/// <c>TBoxVerifyService.VerifyAsync</c>.
/// </summary>
public sealed class DenotationStep(TBoxVerifyService verify) : IPipelineSegment<DenotationInput, DenotationOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));

    public async Task<DenotationOutput> ExecuteAsync(DenotationInput input, CancellationToken cancellationToken)
    {
        var result = await _verify.RunDenotationAsync(
            input.Chunk.Chat, input.Chunk.Text,
            input.Critic.VerifiedDelta.Classes,
            new HashSet<string>(input.Critic.AcceptedNorms, StringComparer.Ordinal),
            input.Critic.CriticState with { Rejections = Array.Empty<RejectedClass>() },
            cancellationToken).ConfigureAwait(false);

        return new DenotationOutput(
            VerifiedDelta: result.Delta,
            Rejections: result.Rejections,
            Recoveries: result.Recoveries,
            DenotationState: result);
    }
}

/// <summary>Bundle for DenotationStep.</summary>
public sealed record DenotationInput(TBoxChunkInput Chunk, CriticOutput Critic);
