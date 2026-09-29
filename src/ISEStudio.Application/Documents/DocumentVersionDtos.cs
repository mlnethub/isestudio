namespace ISEStudio.Application.Documents;

public sealed record DocumentVersionChunkInput(
    int Index,
    string Text,
    int CharStart,
    int CharEnd,
    int TokenEstimate);

public sealed record DocumentVersionInput(
    Guid KnowledgeSystemId,
    Guid DocumentId,
    string ContentSha256,
    IReadOnlyList<DocumentVersionChunkInput> Chunks,
    Guid? FileVersionId = null);

public sealed record DocumentVersionResult(
    Guid Id,
    Guid KnowledgeSystemId,
    Guid DocumentId,
    string ContentSha256,
    int ChunkCount);