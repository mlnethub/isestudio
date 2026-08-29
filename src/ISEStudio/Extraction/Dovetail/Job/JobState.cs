using ISEStudio.Extraction;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using Microsoft.Extensions.AI;

namespace ISEStudio.Extraction.Dovetail.Job;

/// <summary>
/// Immutable per-job state threaded through every Dovetail Job segment.
///
/// <para>Slice 5 Task 1 base shape: identity + LLM client + per-phase
/// accumulators + cancel/error flags. Task 4 R11 extended the record with
/// the per-job closure arguments the phase runners need —
/// <see cref="KsContext"/>, <see cref="Request"/>, <see cref="Chunks"/>,
/// <see cref="PerChunk"/> — so the steps can forward them to the
/// <see cref="ExtractionOrchestrator"/> phase runners without capturing
/// external closures (Dovetail is static-typed; no runtime closure injection).</para>
/// </summary>
public sealed record JobState
{
    public Guid JobId { get; init; }
    public Guid KnowledgeSystemId { get; init; }
    public IReadOnlyList<int> ChunkIds { get; init; } = Array.Empty<int>();
    public IChatClient Chat { get; init; } = null!;
    public JobKind Kind { get; init; }
    public IReadOnlyList<string>? InitialVocabulary { get; init; }

    public IReadOnlyList<ChunkResult> TBoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<ChunkResult> ABoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<int> PerChunkRejections { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> FinalClassVocabulary { get; init; } = Array.Empty<string>();
    public JobTerminology? Terminology { get; init; }
    public long ProcessedChunks { get; init; }

    public string? Error { get; init; }
    public CancellationToken CancellationToken { get; init; }

    // Slice 5 Task 4 R11 — per-job closure fields the steps forward to the
    // phase runners. Nullable + defaulted so the legacy JobStateMutationTests
    // EmptyState helper can still build a state without wiring a real
    // extraction closure; production always populates all four via
    // JobState.From(input).
    public KsContext KsContext { get; init; } = default!;
    public ExtractionRequest Request { get; init; } = default!;
    public IReadOnlyList<ChunkSpan> Chunks { get; init; } = Array.Empty<ChunkSpan>();
    public IReadOnlyList<ChunkVerifyOutcome> PerChunk { get; init; } = Array.Empty<ChunkVerifyOutcome>();

    public bool Succeeded => string.IsNullOrEmpty(Error);
    public bool ShouldSkipRemaining => !Succeeded;

    public static JobState From(JobInput input) => new()
    {
        JobId = input.JobId,
        KnowledgeSystemId = input.KnowledgeSystemId,
        ChunkIds = input.ChunkIds,
        Chat = input.Chat,
        Kind = input.Kind,
        InitialVocabulary = input.InitialVocabulary,
        CancellationToken = input.CancellationToken,
        KsContext = input.KsContext,
        Request = input.Request,
        Chunks = input.Chunks,
        PerChunk = input.PerChunk,
    };
}
