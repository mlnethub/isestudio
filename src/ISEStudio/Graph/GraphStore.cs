using System.Text.Json;
using System.Data;
using System.Data.Common;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace ISEStudio.Graph;

public sealed class GraphStore : IGraphStore
{
    private readonly ISEStudioDbContext _db;
    private readonly IPostgresGraphRepository _repository;

    public GraphStore(ISEStudioDbContext db)
        : this(db, new PostgresGraphRepository(db))
    {
    }

    public GraphStore(ISEStudioDbContext db, IPostgresGraphRepository repository)
    {
        _db = db;
        _repository = repository;
    }

    public Task<GraphEntity> CreateEntityAsync(CreateGraphEntityCommand command, CancellationToken cancellationToken)
    {
        return _repository.CreateEntityAsync(command, cancellationToken);
    }

    public Task<GraphFact> RecordFactAsync(RecordFactCommand command, CancellationToken cancellationToken)
    {
        return _repository.RecordFactAsync(command, cancellationToken);
    }

    public Task InvalidateFactAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset invalidatedAt, CancellationToken cancellationToken)
    {
        return _repository.InvalidateFactAsync(knowledgeSystemId, factId, invalidatedAt, cancellationToken);
    }

    public async Task<GraphNeighborhood> GetNeighborhoodAsync(GraphNeighborhoodQuery query, CancellationToken cancellationToken)
    {
        query.Validate();

        await EnsureGraphEntityInKnowledgeSystemAsync(
            query.RootEntityId,
            query.KnowledgeSystemId,
            "root",
            cancellationToken);

        var connection = _db.Database.GetDbConnection();
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            throw new InvalidOperationException("Neighborhood traversal requires an Npgsql connection.");
        }

        if (npgsqlConnection.State != ConnectionState.Open)
        {
            await npgsqlConnection.OpenAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            """
            WITH RECURSIVE neighborhood (
                id,
                knowledge_system_id,
                subject_entity_id,
                predicate_id,
                object_entity_id,
                object_value,
                confidence,
                valid_from,
                valid_to,
                recorded_at,
                invalidated_at,
                supersedes_fact_id,
                depth,
                current_entity_id,
                path
            ) AS (
                SELECT
                    f.id,
                    f.knowledge_system_id,
                    f.subject_entity_id,
                    f.predicate_id,
                    f.object_entity_id,
                    f.object_value,
                    f.confidence,
                    f.valid_from,
                    f.valid_to,
                    f.recorded_at,
                    f.invalidated_at,
                    f.supersedes_fact_id,
                    1 AS depth,
                    next_entity.id AS current_entity_id,
                    ARRAY[@rootEntityId::uuid, next_entity.id] AS path
                FROM facts f
                JOIN graph_entities subject_entity
                    ON subject_entity.id = f.subject_entity_id
                   AND subject_entity.knowledge_system_id = @knowledgeSystemId
                JOIN graph_entities next_entity
                                        ON next_entity.id = f.object_entity_id
                   AND next_entity.knowledge_system_id = @knowledgeSystemId
                WHERE f.knowledge_system_id = @knowledgeSystemId
                  AND f.object_entity_id IS NOT NULL
                                    AND f.subject_entity_id = @rootEntityId
                  AND NOT next_entity.id = ANY(ARRAY[@rootEntityId::uuid])
                  AND (@includeInvalidated OR f.invalidated_at IS NULL)
                  AND (f.valid_from IS NULL OR f.valid_from <= @effectiveAt)
                  AND (f.valid_to IS NULL OR f.valid_to > @effectiveAt)

                UNION ALL

                SELECT
                    f.id,
                    f.knowledge_system_id,
                    f.subject_entity_id,
                    f.predicate_id,
                    f.object_entity_id,
                    f.object_value,
                    f.confidence,
                    f.valid_from,
                    f.valid_to,
                    f.recorded_at,
                    f.invalidated_at,
                    f.supersedes_fact_id,
                    1 AS depth,
                    next_entity.id AS current_entity_id,
                    ARRAY[@rootEntityId::uuid, next_entity.id] AS path
                FROM facts f
                JOIN graph_entities root_entity
                    ON root_entity.id = f.object_entity_id
                   AND root_entity.knowledge_system_id = @knowledgeSystemId
                JOIN graph_entities next_entity
                    ON next_entity.id = f.subject_entity_id
                   AND next_entity.knowledge_system_id = @knowledgeSystemId
                WHERE f.knowledge_system_id = @knowledgeSystemId
                  AND f.object_entity_id = @rootEntityId
                  AND NOT next_entity.id = ANY(ARRAY[@rootEntityId::uuid])
                  AND (@includeInvalidated OR f.invalidated_at IS NULL)
                  AND (f.valid_from IS NULL OR f.valid_from <= @effectiveAt)
                  AND (f.valid_to IS NULL OR f.valid_to > @effectiveAt)

                UNION ALL

                SELECT
                    f.id,
                    f.knowledge_system_id,
                    f.subject_entity_id,
                    f.predicate_id,
                    f.object_entity_id,
                    f.object_value,
                    f.confidence,
                    f.valid_from,
                    f.valid_to,
                    f.recorded_at,
                    f.invalidated_at,
                    f.supersedes_fact_id,
                    n.depth + 1 AS depth,
                    next_entity.id AS current_entity_id,
                    n.path || next_entity.id AS path
                FROM neighborhood n
                JOIN facts f
                    ON (f.subject_entity_id = n.current_entity_id OR f.object_entity_id = n.current_entity_id)
                JOIN graph_entities subject_entity
                    ON subject_entity.id = f.subject_entity_id
                   AND subject_entity.knowledge_system_id = @knowledgeSystemId
                JOIN graph_entities next_entity
                    ON next_entity.id = CASE
                        WHEN f.subject_entity_id = n.current_entity_id THEN f.object_entity_id
                        ELSE f.subject_entity_id
                    END
                   AND next_entity.knowledge_system_id = @knowledgeSystemId
                WHERE n.depth < @maxDepth
                  AND f.knowledge_system_id = @knowledgeSystemId
                  AND f.object_entity_id IS NOT NULL
                  AND NOT next_entity.id = ANY(n.path)
                  AND (@includeInvalidated OR f.invalidated_at IS NULL)
                  AND (f.valid_from IS NULL OR f.valid_from <= @effectiveAt)
                  AND (f.valid_to IS NULL OR f.valid_to > @effectiveAt)
            )
            SELECT
                id,
                knowledge_system_id,
                subject_entity_id,
                predicate_id,
                object_entity_id,
                object_value,
                confidence,
                valid_from,
                valid_to,
                recorded_at,
                invalidated_at,
                supersedes_fact_id,
                MIN(depth) AS depth
            FROM neighborhood
            GROUP BY
                id,
                knowledge_system_id,
                subject_entity_id,
                predicate_id,
                object_entity_id,
                object_value,
                confidence,
                valid_from,
                valid_to,
                recorded_at,
                invalidated_at,
                supersedes_fact_id
            ORDER BY MIN(depth), id
            """,
            npgsqlConnection);

        if (_db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction transaction)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("knowledgeSystemId", NpgsqlDbType.Uuid, query.KnowledgeSystemId);
        command.Parameters.AddWithValue("rootEntityId", NpgsqlDbType.Uuid, query.RootEntityId);
        command.Parameters.AddWithValue("maxDepth", NpgsqlDbType.Integer, query.MaxDepth);
        command.Parameters.AddWithValue("effectiveAt", NpgsqlDbType.TimestampTz, query.EffectiveAt);
        command.Parameters.AddWithValue("includeInvalidated", NpgsqlDbType.Boolean, query.IncludeInvalidated);

        var factRows = new List<NeighborhoodFactRow>();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                factRows.Add(new NeighborhoodFactRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString(),
                    reader.GetFieldValue<decimal>(6),
                    ReadNullableDateTimeOffset(reader, 7),
                    ReadNullableDateTimeOffset(reader, 8),
                    ReadDateTimeOffset(reader, 9),
                    ReadNullableDateTimeOffset(reader, 10),
                    reader.IsDBNull(11) ? null : reader.GetGuid(11)));
            }
        }

        var factIds = factRows.Select(row => row.Id).Distinct().ToArray();
        var evidenceLookup = factIds.Length == 0
            ? new Dictionary<Guid, IReadOnlyList<GraphFactEvidence>>()
            : (await _db.FactEvidence
                .AsNoTracking()
                .Where(item => factIds.Contains(item.FactId))
                .OrderBy(item => item.FactId)
                .ThenBy(item => item.Id)
                .Select(item => new GraphFactEvidence(item.FactId, item.SourceChunkId, item.Quote, item.Predicate))
                .ToListAsync(cancellationToken))
                .GroupBy(item => item.FactId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<GraphFactEvidence>)group.ToArray());

        var entityIds = factRows
            .SelectMany(row => row.ObjectEntityId is Guid objectEntityId
                ? new[] { row.SubjectEntityId, objectEntityId }
                : new[] { row.SubjectEntityId })
            .Append(query.RootEntityId)
            .Distinct()
            .ToArray();

        var entities = entityIds.Length == 0
            ? Array.Empty<GraphEntity>()
            : await (
                from entity in _db.GraphEntities.AsNoTracking()
                where entity.KnowledgeSystemId == query.KnowledgeSystemId && entityIds.Contains(entity.Id)
                join entityType in _db.EntityTypes.AsNoTracking() on entity.EntityTypeId equals entityType.Id into entityTypes
                from entityType in entityTypes
                    .Where(item => item.KnowledgeSystemId == query.KnowledgeSystemId)
                    .DefaultIfEmpty()
                orderby entity.Id
                select new GraphEntity(entity.Id, entity.KnowledgeSystemId, entityType != null ? entityType.Key : null, entity.Label))
                .ToArrayAsync(cancellationToken);

        var facts = factRows
            .Select(row => new GraphFact(
                row.Id,
                row.KnowledgeSystemId,
                row.SubjectEntityId,
                row.PredicateId,
                row.ObjectEntityId is not null ? GraphObjectKind.Entity : GraphObjectKind.JsonValue,
                row.ObjectEntityId,
                row.ObjectValue,
                row.Confidence,
                row.ValidFrom,
                row.ValidTo,
                row.RecordedAt,
                row.InvalidatedAt is not null
                    ? FactStatus.Invalidated
                    : row.SupersedesFactId is not null
                        ? FactStatus.Superseded
                        : FactStatus.Live,
                row.InvalidatedAt,
                null,
                evidenceLookup.TryGetValue(row.Id, out var evidence)
                    ? evidence
                    : Array.Empty<GraphFactEvidence>()))
            .ToArray();

        return new GraphNeighborhood(
            entities.Select(entity => entity.Id).ToArray(),
            facts,
            entities);
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

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        return ToDateTimeOffset(reader.GetValue(ordinal));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ToDateTimeOffset(reader.GetValue(ordinal));
    }

    private static DateTimeOffset ToDateTimeOffset(object value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime()),
            _ => throw new InvalidOperationException($"Expected a timestamp but received '{value.GetType().Name}'.")
        };
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

    private sealed record NeighborhoodFactRow(
        Guid Id,
        Guid KnowledgeSystemId,
        Guid SubjectEntityId,
        Guid PredicateId,
        Guid? ObjectEntityId,
        string? ObjectValue,
        decimal Confidence,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidTo,
        DateTimeOffset RecordedAt,
        DateTimeOffset? InvalidatedAt,
        Guid? SupersedesFactId);
}