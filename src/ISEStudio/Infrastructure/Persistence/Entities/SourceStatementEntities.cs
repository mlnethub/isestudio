namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class SourceStatementEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public Guid? SourceId { get; set; }
    public string ExternalStatementId { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public string FactKey { get; set; } = string.Empty;
    public string SourceNameSnapshot { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SourceStatementFactEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public Guid SourceStatementId { get; set; }
    public string FactKey { get; set; } = string.Empty;
}