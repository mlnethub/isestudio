namespace ISEStudio.Graph;

public enum GraphObjectKind
{
    Entity,
    JsonValue
}

public enum FactStatus
{
    Live,
    Invalidated,
    Superseded
}

public sealed record CreateGraphEntityCommand(
    Guid KnowledgeSystemId,
    string? TypeKey,
    string? Label,
    string? Description,
    Guid? ActorId);

public sealed record CreateEntityTypeCommand(
    Guid KnowledgeSystemId,
    string Key,
    string? Label,
    string? Description,
    Guid? ActorId);

public sealed record CreateRelationTypeCommand(
    Guid KnowledgeSystemId,
    string Key,
    string? Label,
    string? Description,
    Guid? ActorId);

public sealed record FactEvidenceInput(
    Guid SourceChunkId,
    string Quote,
    string Predicate);

public sealed record RecordFactCommand(
    Guid KnowledgeSystemId,
    Guid SubjectEntityId,
    Guid PredicateId,
    GraphObjectKind ObjectKind,
    Guid? ObjectEntityId,
    string? ObjectValue,
    decimal Confidence,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    IReadOnlyList<FactEvidenceInput> Evidence,
    Guid? ActorId)
{
    public void Validate()
    {
        var hasEntityObject = ObjectEntityId.HasValue;
        var hasValueObject = !string.IsNullOrWhiteSpace(ObjectValue);

        if ((ObjectKind == GraphObjectKind.Entity && (!hasEntityObject || hasValueObject)) ||
            (ObjectKind == GraphObjectKind.JsonValue && (hasEntityObject || !hasValueObject)))
        {
            throw new ArgumentException("A fact must have exactly one object representation.");
        }

        if (Confidence is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(Confidence));
        }

        if (ValidFrom is not null && ValidTo is not null && ValidFrom > ValidTo)
        {
            throw new ArgumentException("ValidFrom cannot be after ValidTo.");
        }
    }
}

public sealed record GraphEntity(
    Guid Id,
    Guid KnowledgeSystemId,
    string? TypeKey,
    string? Label);

public sealed record GraphFactEvidence(
    Guid FactId,
    Guid SourceChunkId,
    string Quote,
    string Predicate);

public sealed record GraphFact(
    Guid Id,
    Guid KnowledgeSystemId,
    Guid SubjectEntityId,
    Guid PredicateId,
    GraphObjectKind ObjectKind,
    Guid? ObjectEntityId,
    string? ObjectValue,
    decimal Confidence,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    FactStatus Status,
    DateTimeOffset? InvalidatedAt,
    Guid? ActorId,
    IReadOnlyList<GraphFactEvidence> Evidence);

public sealed record GraphNeighborhoodQuery(
    Guid KnowledgeSystemId,
    Guid RootEntityId,
    int MaxDepth,
    DateTimeOffset EffectiveAt,
    bool IncludeInvalidated);

public sealed record GraphNeighborhood(
    IReadOnlyList<Guid> EntityIds,
    IReadOnlyList<GraphFact> Facts,
    IReadOnlyList<GraphEntity> Entities);

public interface IGraphStore
{
    Task<GraphEntity> CreateEntityAsync(CreateGraphEntityCommand command, CancellationToken cancellationToken);

    Task<GraphFact> RecordFactAsync(RecordFactCommand command, CancellationToken cancellationToken);

    Task InvalidateFactAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset invalidatedAt, CancellationToken cancellationToken);

    Task<GraphNeighborhood> GetNeighborhoodAsync(GraphNeighborhoodQuery query, CancellationToken cancellationToken);
}