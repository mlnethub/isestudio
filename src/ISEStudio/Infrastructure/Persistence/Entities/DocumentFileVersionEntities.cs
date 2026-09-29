namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class DocumentFileVersionEntity : EntityBase
{
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset? DocTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DocumentFileVersionSnapshotEntity : EntityBase
{
    public Guid DocumentFileVersionId { get; set; }
    public Guid DocumentVersionId { get; set; }
}