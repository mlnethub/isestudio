using ISEStudio.Extraction;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using Microsoft.Extensions.AI;

namespace ISEStudio.Extraction.Dovetail.Job;

public enum JobKind { TBoxOnly, ABoxOnly, Combined }

/// <summary>
/// Immutable Job entry shape handed to <see cref="JobPipelineRouter"/>.
///
/// <para>Slice 5 Task 1 base shape: identity fields plus the LLM client and
/// the cancellation token. Task 4 R11 extended the record with the
/// per-job closure arguments the phase runners need — <see cref="KsContext"/>,
/// <see cref="Request"/>, <see cref="Chunks"/>, <see cref="PerChunk"/> —
/// because Dovetail is static-typed: the pipeline carries no runtime
/// closure, so every field the steps forward into the phase runners must
/// ride on <see cref="JobState"/>.</para>
/// </summary>
public sealed record JobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<int> ChunkIds,
    IChatClient Chat,
    JobKind Kind,
    IReadOnlyList<string>? InitialVocabulary,
    CancellationToken CancellationToken,
    KsContext KsContext,
    ExtractionRequest Request,
    IReadOnlyList<ChunkSpan> Chunks,
    IReadOnlyList<ChunkVerifyOutcome> PerChunk);
