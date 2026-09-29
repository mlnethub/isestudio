namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class SourceDocumentBindingEntity : EntityBase
{
    public Guid SourceId { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public Guid DocumentId { get; set; }
    public DateTimeOffset? MissingSince { get; set; }
}