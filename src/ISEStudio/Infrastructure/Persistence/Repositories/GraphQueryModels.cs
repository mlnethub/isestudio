namespace ISEStudio.Infrastructure.Persistence.Repositories;

public sealed record FactQuery(
    Guid? SubjectEntityId = null,
    Guid? PredicateId = null,
    Guid? ObjectEntityId = null,
    bool IncludeInvalidated = false);

public sealed record GraphEntityType(
    Guid Id,
    Guid KnowledgeSystemId,
    string Iri,
    string Key,
    string? Label,
    string? Description);

public sealed record GraphRelationType(
    Guid Id,
    Guid KnowledgeSystemId,
    string Iri,
    string Key,
    string? Label,
    string? Description);