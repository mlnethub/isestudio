using System.Text.Json;
using System.Data;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ISEStudio.Graph;

public sealed class GraphStore : IGraphStore
{
    private readonly ISEStudioDbContext _db;

    public GraphStore(ISEStudioDbContext db)
    {
        _db = db;
    }

    public Task<GraphEntity> CreateEntityAsync(CreateGraphEntityCommand command, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Graph entity creation is outside task 3 scope.");
    }

    public async Task<GraphFact> RecordFactAsync(RecordFactCommand command, CancellationToken cancellationToken)
    {
        command.Validate();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var knowledgeSystemExists = await _db.KnowledgeSystems
            .AnyAsync(item => item.Id == command.KnowledgeSystemId, cancellationToken);
        if (!knowledgeSystemExists)
        {
            throw new KeyNotFoundException($"Knowledge system '{command.KnowledgeSystemId}' was not found.");
        }

        await EnsureGraphEntityInKnowledgeSystemAsync(
            command.SubjectEntityId,
            command.KnowledgeSystemId,
            "subject",
            cancellationToken);

        await EnsureRelationTypeInKnowledgeSystemAsync(
            command.PredicateId,
            command.KnowledgeSystemId,
            cancellationToken);

        if (command.ObjectKind == GraphObjectKind.Entity && command.ObjectEntityId is Guid objectEntityId)
        {
            await EnsureGraphEntityInKnowledgeSystemAsync(
                objectEntityId,
                command.KnowledgeSystemId,
                "object",
                cancellationToken);
        }

        await EnsureEvidenceChunksInKnowledgeSystemAsync(
            command.Evidence,
            command.KnowledgeSystemId,
            cancellationToken);

        if (command.ActorId is Guid actorId)
        {
            var actorExists = await _db.Users.AnyAsync(item => item.Id == actorId, cancellationToken);
            if (!actorExists)
            {
                throw new KeyNotFoundException($"Actor '{actorId}' was not found.");
            }
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
            InvalidatedAt = null,
            SupersedesFactId = null,
        };

        var evidence = command.Evidence
            .Select(item => new FactEvidenceEntity
            {
                Id = Guid.NewGuid(),
                FactId = fact.Id,
                SourceChunkId = item.SourceChunkId,
                Quote = item.Quote,
                Predicate = item.Predicate,
            })
            .ToArray();

        var auditEvent = new AuditEventEntity
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
        };

        _db.Facts.Add(fact);
        _db.FactEvidence.AddRange(evidence);
        _db.AuditEvents.Add(auditEvent);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MapFact(fact, evidence);
    }

    public async Task InvalidateFactAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset invalidatedAt, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var factWasInvalidated = await InvalidateLiveFactRowAsync(
            transaction,
            knowledgeSystemId,
            factId,
            invalidatedAt,
            cancellationToken);

        if (!factWasInvalidated)
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
            Detail = JsonSerializer.SerializeToDocument(new
            {
                factId,
                knowledgeSystemId,
                invalidatedAt,
            }),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<GraphNeighborhood> GetNeighborhoodAsync(GraphNeighborhoodQuery query, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Graph neighborhood traversal is outside task 3 scope.");
    }

    private async Task EnsureGraphEntityInKnowledgeSystemAsync(Guid entityId, Guid knowledgeSystemId, string role, CancellationToken cancellationToken)
    {
        var exists = await _db.GraphEntities.AnyAsync(item =>
            item.Id == entityId &&
            item.KnowledgeSystemId == knowledgeSystemId,
            cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"The {role} entity '{entityId}' was not found in knowledge system '{knowledgeSystemId}'.");
        }
    }

    private async Task EnsureRelationTypeInKnowledgeSystemAsync(Guid predicateId, Guid knowledgeSystemId, CancellationToken cancellationToken)
    {
        var exists = await _db.RelationTypes.AnyAsync(item =>
            item.Id == predicateId &&
            item.KnowledgeSystemId == knowledgeSystemId,
            cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Relation type '{predicateId}' was not found in knowledge system '{knowledgeSystemId}'.");
        }
    }

    private async Task EnsureEvidenceChunksInKnowledgeSystemAsync(
        IReadOnlyList<FactEvidenceInput> evidence,
        Guid knowledgeSystemId,
        CancellationToken cancellationToken)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        var chunkIds = evidence
            .Select(item => item.SourceChunkId)
            .Distinct()
            .ToArray();

        var chunkKnowledgeSystems = await (
            from chunk in _db.Chunks
            join document in _db.Documents on chunk.DocumentId equals document.Id
            where chunkIds.Contains(chunk.Id)
            select new
            {
                chunk.Id,
                document.KnowledgeSystemId,
            })
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

    private async Task<bool> InvalidateLiveFactRowAsync(
        IDbContextTransaction transaction,
        Guid knowledgeSystemId,
        Guid factId,
        DateTimeOffset invalidatedAt,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            UPDATE facts
            SET invalidated_at = @invalidatedAt
            WHERE id = @factId
              AND knowledge_system_id = @knowledgeSystemId
              AND invalidated_at IS NULL
            RETURNING id
            """;

        command.Parameters.Add(CreateParameter(command, "invalidatedAt", invalidatedAt));
        command.Parameters.Add(CreateParameter(command, "factId", factId));
        command.Parameters.Add(CreateParameter(command, "knowledgeSystemId", knowledgeSystemId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static IDbDataParameter CreateParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
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
}