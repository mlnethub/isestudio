using Microsoft.Extensions.AI;

namespace ISEStudio.Extraction.Dovetail.Job;

public enum JobKind { TBoxOnly, ABoxOnly, Combined }

public sealed record JobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<int> ChunkIds,
    IChatClient Chat,
    JobKind Kind,
    IReadOnlyList<string>? InitialVocabulary,
    CancellationToken CancellationToken);