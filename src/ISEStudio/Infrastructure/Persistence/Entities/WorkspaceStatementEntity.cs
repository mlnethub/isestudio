namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// One RDF-compatible workspace statement. PostgreSQL is authoritative;
/// parser/serializer implementations only translate at the boundary.
/// </summary>
public sealed class WorkspaceStatementEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public string Layer { get; set; } = string.Empty;

    public string? GraphIri { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string SubjectKind { get; set; } = "iri";

    public string Predicate { get; set; } = string.Empty;

    public string Object { get; set; } = string.Empty;

    public string ObjectKind { get; set; } = "iri";

    public string? Language { get; set; }

    public string? Datatype { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
