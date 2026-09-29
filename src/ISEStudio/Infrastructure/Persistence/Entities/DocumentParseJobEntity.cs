namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class DocumentParseJobEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? SourceId { get; set; }
    public Guid DocumentFileVersionId { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}