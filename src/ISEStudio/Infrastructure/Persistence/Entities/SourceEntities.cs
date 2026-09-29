namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class SourceEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public string Kind { get; set; } = "folder";
    public string Name { get; set; } = string.Empty;
    public string Config { get; set; } = "{}";
    public string? Icon { get; set; }
    public int? SyncIntervalMinutes { get; set; }
    public string? SyncCron { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string LastSyncStatus { get; set; } = "never";
    public string? LastSyncError { get; set; }
    public int LastSyncAdded { get; set; }
    public string? IngestTokenCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}