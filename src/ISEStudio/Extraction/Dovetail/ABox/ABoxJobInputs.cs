using ISEStudio.Ontology;  // for ConflictDetection.DetectedConflict
using Microsoft.Extensions.AI;  // for IChatClient + IEmbeddingGenerator<,>

namespace ISEStudio.Extraction.Dovetail.ABox;

/// <summary>A single duplicate-class candidate pair produced by candidate generation (Jaccard + cosine sim).</summary>
public sealed record CandidatePair(string IriA, string IriB, double? Cosine);

/// <summary>Result of CandidateGenerationStep: all candidate pairs above the similarity floor.</summary>
public sealed record CandidateList(IReadOnlyList<CandidatePair> Pairs);

/// <summary>Result of JudgeStep: indices into <see cref="CandidateList.Pairs"/> that the LLM judge kept as true duplicates.</summary>
public sealed record JudgeResult(IReadOnlyList<int> KeptIndices, string? Reason);

/// <summary>A single applied class merge: source class is retyped into target with the judge confidence.</summary>
public sealed record MergedClassPair(string Source, string Target, double Confidence);

/// <summary>Result of MergeApplyStep: all class pairs that were committed to the ABox.</summary>
public sealed record AppliedMerges(IReadOnlyList<MergedClassPair> Pairs);

/// <summary>Result of ConflictDetectionStep: conflicts that remain after merges were applied.</summary>
public sealed record RemainingConflicts(IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts);

/// <summary>Result of CascadeRetypeStep: ABox individuals that were retargeted as a side-effect of the merges.</summary>
public sealed record CascadeResult(IReadOnlyList<Guid> UpdatedIndividuals);

/// <summary>Output of <see cref="ABoxJobPipeline"/>: applied merges + remaining conflicts + cascade-retype individuals.</summary>
public sealed record ABoxJobResult(
    AppliedMerges Applied,
    RemainingConflicts Remaining,
    CascadeResult Cascade);

/// <summary>Output of MergeApplyStep before conflict detection + cascade re-type: applied merges + remaining conflicts snapshot.</summary>
public sealed record MergeApplyOutput(
    AppliedMerges Applied,
    RemainingConflicts Remaining);

/// <summary>Input to <see cref="ABoxJobPipeline"/>: job identity + RDF store + LLM clients + confidence floor.</summary>
public sealed record ABoxJobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    string GraphIri,
    StoreWrapper Store,
    IChatClient Chat,
    IEmbeddingGenerator<string, Embedding<float>> Embedder,
    double MinConfidence);