namespace ISEStudio.Infrastructure.Persistence.Entities;

public sealed class EntityTypeEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string? Description { get; set; }
}

public sealed class RelationTypeEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string? Description { get; set; }
}

public sealed class RelationTypeDomainEntity
{
    public Guid RelationTypeId { get; set; }

    public Guid EntityTypeId { get; set; }
}

public sealed class RelationTypeRangeEntity
{
    public Guid RelationTypeId { get; set; }

    public Guid EntityTypeId { get; set; }
}

public sealed class EntityTypeParentEntity
{
    public Guid EntityTypeId { get; set; }

    public Guid ParentEntityTypeId { get; set; }
}

public sealed class GraphEntityEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public Guid? EntityTypeId { get; set; }

    public string? Label { get; set; }

    public string? Description { get; set; }
}

public sealed class FactEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }

    public Guid SubjectEntityId { get; set; }

    public Guid PredicateId { get; set; }

    public Guid? ObjectEntityId { get; set; }

    public string? ObjectValue { get; set; }

    public decimal Confidence { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public DateTimeOffset? InvalidatedAt { get; set; }

    public Guid? SupersedesFactId { get; set; }
}

public sealed class FactEvidenceEntity : EntityBase
{
    public Guid FactId { get; set; }

    public Guid SourceChunkId { get; set; }

    public string Quote { get; set; } = string.Empty;

    public string Predicate { get; set; } = string.Empty;
}

public sealed class FactConflictEntity
{
    public Guid FactId { get; set; }

    public Guid ConflictId { get; set; }
}