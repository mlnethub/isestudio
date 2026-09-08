using System.Text.Json;

namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class OntologyAxiomEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public string SubjectIri { get; set; } = string.Empty;

    public string PredicateIri { get; set; } = string.Empty;

    public string? ObjectIri { get; set; }

    public string? ObjectValue { get; set; }

    public JsonDocument? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}