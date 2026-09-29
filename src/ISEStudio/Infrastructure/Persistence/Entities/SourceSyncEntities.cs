namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class SourceSyncJobEntity : EntityBase
{
    public Guid SourceId { get; set; }
    public string Status { get; set; } = "queued";
    public Guid? ActiveRunId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class SourceSyncRunEntity : EntityBase
{
    public Guid SourceId { get; set; }
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public string? Error { get; set; }
}