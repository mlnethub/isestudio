namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class DocumentVersionEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public Guid DocumentId { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DocumentVersionChunkEntity : EntityBase
{
    public Guid DocumentVersionId { get; set; }
    public int Idx { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CharStart { get; set; }
    public int CharEnd { get; set; }
    public int TokenEstimate { get; set; }
}