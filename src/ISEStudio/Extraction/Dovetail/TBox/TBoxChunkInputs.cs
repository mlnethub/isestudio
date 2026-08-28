using Microsoft.Extensions.AI;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>Input to <see cref="TBoxChunkPipeline"/>: one chunk's text + the extracted TBox delta.</summary>
public sealed record TBoxChunkInput(
    int ChunkId,
    string Text,
    TBoxDelta Delta,
    IChatClient Chat);

/// <summary>Result of TBoxChunkPipeline.CriticStep: filtered delta + accepted norms + raw critic rejections.</summary>
public sealed record CriticOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlySet<string> AcceptedNorms,
    IReadOnlyList<RejectedClass> CriticRejections,
    TBoxVerifyResult CriticState);

/// <summary>
/// Result of TBoxChunkPipeline.AdjudicatorStep:
/// <list type="bullet">
///   <item><description><see cref="Succeeded"/>: false means the adjudicator call threw and the step caught the exception.</description></item>
///   <item><description><see cref="Recovered"/>: classes the adjudicator accepted (only meaningful when Succeeded is true).</description></item>
///   <item><description><see cref="DenotationFallback"/>: pre-computed denotation over the original chunk delta when Succeeded is false. ChunkMergeStep uses this directly instead of the DenotationStep output.</description></item>
/// </list>
/// </summary>
public sealed record AdjudicatorOutput(
    bool Succeeded,
    IReadOnlyList<ClassMutation> Recovered,
    TBoxVerifyResult? DenotationFallback);

/// <summary>Result of TBoxChunkPipeline.DenotationStep: verified delta + final rejections + recoveries.</summary>
public sealed record DenotationOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlyList<RejectedClass> Rejections,
    IReadOnlyList<RecoveredClass> Recoveries,
    TBoxVerifyResult DenotationState);

/// <summary>Input to <see cref="TBoxJobPipeline"/>: per-chunk verify results + per-chunk rejections + final class vocabulary.</summary>
public sealed record TBoxJobInput(
    Guid JobId,
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    IReadOnlyList<CorpusRecoveryChunk> PerChunkRejections,
    IReadOnlyList<string> FinalClassVocabulary,
    IReadOnlyList<string> PerChunkText,
    IChatClient Chat);

/// <summary>Output of TBoxJobPipeline: chunk results + corpus recovery + hierarchy recovery.</summary>
public sealed record TBoxJobResult(
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    CorpusRecoveryResult Corpus,
    HierarchyRecoveryResult Hierarchy);

/// <summary>Wrapper emitted by TBoxJobPipeline.CorpusRecoveryStep (allows OptionalSegment to return Empty).</summary>
public sealed record CorpusRecoverySegmentOutput(
    CorpusRecoveryResult Result,
    bool Enabled);

/// <summary>Wrapper emitted by TBoxJobPipeline.HierarchyRecoveryStep.</summary>
public sealed record HierarchyRecoverySegmentOutput(
    HierarchyRecoveryResult Result,
    bool Enabled);