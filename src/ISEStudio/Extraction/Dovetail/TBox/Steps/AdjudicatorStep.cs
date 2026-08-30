using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ISEStudio.Extraction.Dovetail.TBox.Steps;

/// <summary>
/// Step 2 of TBoxChunkPipeline. Catches its own adjudicator exception and
/// falls back to denotation over the ORIGINAL chunk delta (not the
/// critic-filtered subset), matching the fail-soft branch of
/// <c>TBoxVerifyService.VerifyAsync</c>. The outer <c>FailSoftSegment</c>
/// wrapper that earlier drafts proposed is unnecessary — this step already
/// never throws on adjudicator failure.
///
/// <para>Observability: when the catch fires, the failure is logged at one
/// of two levels keyed off whether the inner threw an
/// <see cref="OperationCanceledException"/> with cancellation NOT
/// requested (System.ClientModel <c>NetworkTimeout</c> fingerprint) vs
/// an operational failure. SDK timeouts emit
/// <see cref="LogLevel.Information"/> because the inner
/// <c>LlmCallDiagnostics.LogCancellation</c> already fired a
/// <see cref="LogLevel.Warning"/> with the precise
/// <c>operationName / elapsedSeconds / configuredTimeoutSec /
/// isCallerCancelled</c> shape; this line just notes the fail-soft
/// fallback is engaged. Operational failures (JsonException, network
/// errors, unhandled bugs) emit <see cref="LogLevel.Warning"/> with the
/// exception payload so dashboards keep paging. Field names are
/// <c>SecretRedactionProcessor</c>-safe (no
/// <c>"token"</c>/<c>"prompt"</c>/<c>"secret"</c>/<c>"bearer"</c>
/// substring) — same hygiene rule as
/// <c>LlmCallDiagnostics.LogCancellation</c>, see commit <c>dd6b418</c>.</para>
/// </summary>
public sealed class AdjudicatorStep(
    TBoxVerifyService verify,
    ILogger<AdjudicatorStep>? logger = null)
    : IPipelineSegment<TBoxChunkInput, CriticOutput, AdjudicatorOutput>
{
    private readonly TBoxVerifyService _verify = verify ?? throw new ArgumentNullException(nameof(verify));
    private readonly ILogger<AdjudicatorStep> _logger = logger ?? NullLogger<AdjudicatorStep>.Instance;

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
            //
            // Log BEFORE the denotation fallback so dashboards see the
            // adjudicator failure even if the fallback itself throws (in
            // which case the exception bubbles up to FailSoftSegment /
            // pipeline top — and that level's log will join on the same
            // disputedClassCount for root-cause correlation).
            var exceptionType = ex.GetType().FullName;
            var innerExceptionType = ex.InnerException?.GetType().FullName ?? "<none>";
            var isSdkTimeoutCancellation = ex is OperationCanceledException
                && !cancellationToken.IsCancellationRequested;
            if (isSdkTimeoutCancellation)
            {
                _logger.LogInformation(
                    "AdjudicatorStep failed fail-soft (SDK timeout); falling back to denotation on original chunk delta " +
                    "(disputedClassCount={DisputedClassCount}, chunkDeltaClassCount={ChunkDeltaClassCount}, " +
                    "exceptionType={ExceptionType}, innerExceptionType={InnerExceptionType}, " +
                    "cancellationRequested={CancellationRequested})",
                    disputed.Count,
                    chunk.Delta.Classes.Count,
                    exceptionType,
                    innerExceptionType,
                    cancellationToken.IsCancellationRequested);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "AdjudicatorStep failed fail-soft (operational failure); falling back to denotation on original chunk delta " +
                    "(disputedClassCount={DisputedClassCount}, chunkDeltaClassCount={ChunkDeltaClassCount}, " +
                    "exceptionType={ExceptionType}, innerExceptionType={InnerExceptionType}, " +
                    "cancellationRequested={CancellationRequested})",
                    disputed.Count,
                    chunk.Delta.Classes.Count,
                    exceptionType,
                    innerExceptionType,
                    cancellationToken.IsCancellationRequested);
            }

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