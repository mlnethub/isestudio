using Microsoft.Extensions.AI;

namespace ISEStudio.Extraction.Dovetail.Job;

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
    };
}