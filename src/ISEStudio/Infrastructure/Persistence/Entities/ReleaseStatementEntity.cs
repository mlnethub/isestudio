using System.Text.Json;

namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class ReleaseStatementEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public Guid ReleaseId { get; set; }

    public string Layer { get; set; } = string.Empty;

    public string? GraphIri { get; set; }

    public string SubjectIri { get; set; } = string.Empty;

    public string PredicateIri { get; set; } = string.Empty;

    public string? ObjectIri { get; set; }

    public string? ObjectValue { get; set; }

    public string StatementHash { get; set; } = string.Empty;

    public JsonDocument? Payload { get; set; }
}