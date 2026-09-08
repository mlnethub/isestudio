using System.Text.Json;
using ISEStudio.Graph;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Infrastructure.Persistence.Repositories;

public sealed class PostgresGraphRepository : IPostgresGraphRepository
{
    private readonly ISEStudioDbContext _db;

    public PostgresGraphRepository(ISEStudioDbContext db)
    {
        _db = db;
    }

    public async Task<GraphEntityType> CreateEntityTypeAsync(
        CreateEntityTypeCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var knowledgeSystem = await RequireKnowledgeSystemAsync(command.KnowledgeSystemId, cancellationToken);
        var entity = new EntityTypeEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = command.KnowledgeSystemId,
            Iri = BuildTypeIri(knowledgeSystem.BaseIri, command.Key),
            Key = command.Key,
            Label = command.Label,
            Description = command.Description,
        };
        _db.EntityTypes.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GraphEntityType(entity.Id, entity.KnowledgeSystemId, entity.Iri, entity.Key, entity.Label, entity.Description);
    }

    public async Task<GraphRelationType> CreateRelationTypeAsync(
        CreateRelationTypeCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var knowledgeSystem = await RequireKnowledgeSystemAsync(command.KnowledgeSystemId, cancellationToken);
        var relation = new RelationTypeEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = command.KnowledgeSystemId,
            Iri = BuildTypeIri(knowledgeSystem.BaseIri, command.Key),
            Key = command.Key,
            Label = command.Label,
            Description = command.Description,
        };
        _db.RelationTypes.Add(relation);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GraphRelationType(relation.Id, relation.KnowledgeSystemId, relation.Iri, relation.Key, relation.Label, relation.Description);
    }

    public async Task<GraphEntity> CreateEntityAsync(
        CreateGraphEntityCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var knowledgeSystem = await _db.KnowledgeSystems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == command.KnowledgeSystemId, cancellationToken);
        if (knowledgeSystem is null)
        {
            throw new KeyNotFoundException($"Knowledge system '{command.KnowledgeSystemId}' was not found.");
        }

        EntityTypeEntity? entityType = null;
        if (!string.IsNullOrWhiteSpace(command.TypeKey))
        {
            entityType = await _db.EntityTypes
                .SingleOrDefaultAsync(item =>
                    item.KnowledgeSystemId == command.KnowledgeSystemId &&
                    item.Key == command.TypeKey,
                    cancellationToken);
            if (entityType is null)
            {
                throw new KeyNotFoundException(
                    $"Entity type '{command.TypeKey}' was not found in knowledge system '{command.KnowledgeSystemId}'.");
            }
        }

        var entityId = Guid.NewGuid();
        var entity = new GraphEntityEntity
        {
            Id = entityId,
            KnowledgeSystemId = command.KnowledgeSystemId,
            Iri = BuildEntityIri(knowledgeSystem.BaseIri, entityId),
            EntityTypeId = entityType?.Id,
            Label = command.Label,
            Description = command.Description,
        };

        _db.GraphEntities.Add(entity);
        _db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = command.KnowledgeSystemId,
            ActorId = command.ActorId,
            ActorName = string.Empty,
            Action = "graph.entity.created",
            Summary = "Created graph entity.",
            Detail = JsonSerializer.SerializeToDocument(new
            {
                entityId,
                entityIri = entity.Iri,
                entityTypeId = entity.EntityTypeId,
            }),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GraphEntity(
            entity.Id,
            entity.KnowledgeSystemId,
            entityType?.Key,
            entity.Label);
    }

    public async Task<GraphFact> RecordFactAsync(
        RecordFactCommand command,
        CancellationToken cancellationToken)
    {
        command.Validate();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await RequireKnowledgeSystemAsync(command.KnowledgeSystemId, cancellationToken);
        await RequireEntityAsync(command.SubjectEntityId, command.KnowledgeSystemId, "subject", cancellationToken);
        await RequireRelationAsync(command.PredicateId, command.KnowledgeSystemId, cancellationToken);
        if (command.ObjectEntityId is Guid objectEntityId)
        {
            await RequireEntityAsync(objectEntityId, command.KnowledgeSystemId, "object", cancellationToken);
        }
        await EnsureEvidenceChunksInKnowledgeSystemAsync(command.Evidence, command.KnowledgeSystemId, cancellationToken);
        if (command.ActorId is Guid actorId && !await _db.Users.AnyAsync(item => item.Id == actorId, cancellationToken))
        {
            throw new KeyNotFoundException($"Actor '{actorId}' was not found.");
        }

        var fact = new FactEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = command.KnowledgeSystemId,
            SubjectEntityId = command.SubjectEntityId,
            PredicateId = command.PredicateId,
            ObjectEntityId = command.ObjectEntityId,
            ObjectValue = command.ObjectValue,
            Confidence = command.Confidence,
            ValidFrom = command.ValidFrom,
            ValidTo = command.ValidTo,
            RecordedAt = command.RecordedAt,
        };
        var evidence = command.Evidence.Select(item => new FactEvidenceEntity
        {
            Id = Guid.NewGuid(),
            FactId = fact.Id,
            SourceChunkId = item.SourceChunkId,
            Quote = item.Quote,
            Predicate = item.Predicate,
        }).ToArray();
        _db.Facts.Add(fact);
        _db.FactEvidence.AddRange(evidence);
        _db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = command.KnowledgeSystemId,
            ActorId = command.ActorId,
            ActorName = string.Empty,
            Action = "graph.fact.recorded",
            Summary = "Recorded graph fact.",
            Detail = JsonSerializer.SerializeToDocument(new
            {
                factId = fact.Id,
                knowledgeSystemId = command.KnowledgeSystemId,
                actorId = command.ActorId,
                evidenceCount = evidence.Length,
            }),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapFact(fact, evidence);
    }

    public async Task InvalidateFactAsync(
        Guid knowledgeSystemId,
        Guid factId,
        DateTimeOffset invalidatedAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await _db.Facts
            .Where(item =>
                item.Id == factId &&
                item.KnowledgeSystemId == knowledgeSystemId &&
                item.InvalidatedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.InvalidatedAt, invalidatedAt),
                cancellationToken);
        if (updated != 1)
        {
            throw new KeyNotFoundException($"Fact '{factId}' was not found or already invalidated.");
        }
        _db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = knowledgeSystemId,
            ActorName = string.Empty,
            Action = "graph.fact.invalidated",
            Summary = "Invalidated graph fact.",
            Detail = JsonSerializer.SerializeToDocument(new { factId, knowledgeSystemId, invalidatedAt }),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GraphFact>> QueryFactsAsync(
        Guid knowledgeSystemId,
        FactQuery query,
        CancellationToken cancellationToken)
    {
        var factsQuery = _db.Facts.AsNoTracking()
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId);
        if (!query.IncludeInvalidated)
        {
            factsQuery = factsQuery.Where(item => item.InvalidatedAt == null);
        }
        if (query.SubjectEntityId is Guid subjectEntityId)
        {
            factsQuery = factsQuery.Where(item => item.SubjectEntityId == subjectEntityId);
        }
        if (query.PredicateId is Guid predicateId)
        {
            factsQuery = factsQuery.Where(item => item.PredicateId == predicateId);
        }
        if (query.ObjectEntityId is Guid objectEntityId)
        {
            factsQuery = factsQuery.Where(item => item.ObjectEntityId == objectEntityId);
        }
        var rows = await factsQuery.OrderBy(item => item.RecordedAt).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.Id).ToArray();
        var evidence = await _db.FactEvidence.AsNoTracking()
            .Where(item => ids.Contains(item.FactId))
            .ToListAsync(cancellationToken);
        return rows.Select(item => MapFact(item, evidence.Where(evidenceItem => evidenceItem.FactId == item.Id).ToArray())).ToArray();
    }

    private async Task<KnowledgeSystemEntity> RequireKnowledgeSystemAsync(Guid knowledgeSystemId, CancellationToken cancellationToken)
    {
        var knowledgeSystem = await _db.KnowledgeSystems.SingleOrDefaultAsync(item => item.Id == knowledgeSystemId, cancellationToken);
        return knowledgeSystem ?? throw new KeyNotFoundException($"Knowledge system '{knowledgeSystemId}' was not found.");
    }

    private async Task RequireEntityAsync(Guid entityId, Guid knowledgeSystemId, string role, CancellationToken cancellationToken)
    {
        if (!await _db.GraphEntities.AnyAsync(item => item.Id == entityId && item.KnowledgeSystemId == knowledgeSystemId, cancellationToken))
        {
            throw new KeyNotFoundException($"The {role} entity '{entityId}' was not found in knowledge system '{knowledgeSystemId}'.");
        }
    }

    private async Task RequireRelationAsync(Guid relationId, Guid knowledgeSystemId, CancellationToken cancellationToken)
    {
        if (!await _db.RelationTypes.AnyAsync(item => item.Id == relationId && item.KnowledgeSystemId == knowledgeSystemId, cancellationToken))
        {
            throw new KeyNotFoundException($"Relation type '{relationId}' was not found in knowledge system '{knowledgeSystemId}'.");
        }
    }

    private async Task EnsureEvidenceChunksInKnowledgeSystemAsync(
        IReadOnlyList<FactEvidenceInput> evidence,
        Guid knowledgeSystemId,
        CancellationToken cancellationToken)
    {
        var chunkIds = evidence.Select(item => item.SourceChunkId).Distinct().ToArray();
        if (chunkIds.Length == 0)
        {
            return;
        }

        var chunkKnowledgeSystems = await (
            from chunk in _db.Chunks
            join document in _db.Documents on chunk.DocumentId equals document.Id
            where chunkIds.Contains(chunk.Id)
            select new { chunk.Id, document.KnowledgeSystemId })
            .ToDictionaryAsync(item => item.Id, item => item.KnowledgeSystemId, cancellationToken);

        foreach (var chunkId in chunkIds)
        {
            if (!chunkKnowledgeSystems.TryGetValue(chunkId, out var chunkKnowledgeSystemId))
            {
                throw new InvalidOperationException($"Evidence chunk '{chunkId}' was not found.");
            }
            if (chunkKnowledgeSystemId != knowledgeSystemId)
            {
                throw new InvalidOperationException(
                    $"Evidence chunk '{chunkId}' does not belong to knowledge system '{knowledgeSystemId}'.");
            }
        }
    }

    private static string BuildTypeIri(string baseIri, string key)
    {
        var normalizedBase = baseIri.TrimEnd('#', '/');
        return $"{normalizedBase}/{key.TrimStart('#', '/') }";
    }

    private static GraphFact MapFact(FactEntity fact, IReadOnlyList<FactEvidenceEntity> evidence)
    {
        var status = fact.InvalidatedAt is not null
            ? FactStatus.Invalidated
            : fact.SupersedesFactId is not null
                ? FactStatus.Superseded
                : FactStatus.Live;
        return new GraphFact(
            fact.Id,
            fact.KnowledgeSystemId,
            fact.SubjectEntityId,
            fact.PredicateId,
            fact.ObjectEntityId is not null ? GraphObjectKind.Entity : GraphObjectKind.JsonValue,
            fact.ObjectEntityId,
            fact.ObjectValue,
            fact.Confidence,
            fact.ValidFrom,
            fact.ValidTo,
            fact.RecordedAt,
            status,
            fact.InvalidatedAt,
            null,
            evidence.Select(item => new GraphFactEvidence(item.FactId, item.SourceChunkId, item.Quote, item.Predicate)).ToArray());
    }

    private static string BuildEntityIri(string baseIri, Guid entityId)
    {
        var normalizedBase = baseIri.TrimEnd('#', '/');
        return $"{normalizedBase}/entity/{entityId:N}";
    }
}
