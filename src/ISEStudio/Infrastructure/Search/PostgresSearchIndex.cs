using System.Data.Common;
using System.Data;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Search;
using ISEStudio.Infrastructure.Persistence;

namespace ISEStudio.Infrastructure.Search;

/// <summary>
/// PostgreSQL-backed lexical search over immutable document-version chunks.
/// PostgreSQL remains authoritative; no local or secondary index is used.
/// </summary>
public sealed class PostgresSearchIndex : ISearchIndex
{
    private const string SearchSql = """
        WITH ranked_versions AS (
            SELECT
                dv.id,
                dv.document_id,
                dv.knowledge_system_id,
                ROW_NUMBER() OVER (
                    PARTITION BY dv.document_id
                    ORDER BY dv.created_at DESC, dv.id DESC) AS version_rank
            FROM document_version AS dv
            WHERE dv.knowledge_system_id = @knowledge_system_id
              AND (@as_of IS NULL OR dv.created_at <= @as_of))
        SELECT
            dvc.id,
            rv.document_id,
            rv.id,
            dvc.text,
            ts_rank_cd(
                to_tsvector('simple', dvc.text),
                websearch_to_tsquery('simple', @query)) AS lexical_score,
            d."Sha256"
        FROM document_version_chunk AS dvc
        INNER JOIN ranked_versions AS rv
            ON rv.id = dvc.document_version_id
           AND rv.version_rank = 1
        INNER JOIN document AS d
            ON d."id" = rv.document_id
           AND d."KnowledgeSystemId" = rv.knowledge_system_id
        INNER JOIN knowledgesystem AS ks ON ks."id" = rv.knowledge_system_id
        LEFT JOIN ksgrant AS grant_row
                    ON grant_row."KnowledgeSystemId" = rv.knowledge_system_id
                 AND grant_row."UserId" = @actor_id
        WHERE to_tsvector('simple', dvc.text) @@
              websearch_to_tsquery('simple', @query)
          AND (
              ks."OwnerId" = @actor_id
              OR grant_row."UserId" IS NOT NULL)
        ORDER BY lexical_score DESC, dvc.id ASC
        LIMIT @limit
        OFFSET @offset
        """;

    private readonly IDbContextFactory<ISEStudioDbContext> _contextFactory;

    public PostgresSearchIndex(IDbContextFactory<ISEStudioDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public SearchCapabilities Capabilities => SearchCapabilities.NoVectorSearch;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.QueryVector is not null)
        {
            throw new NotSupportedException(
                "Vector search is unavailable because this schema has no vector column or pgvector capability.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = SearchSql;
        AddParameter(command, "@query", request.Query, DbType.String);
        AddParameter(command, "@knowledge_system_id", request.KnowledgeSystemId, DbType.Guid);
        AddParameter(command, "@actor_id", request.ActorId, DbType.Guid);
        AddParameter(command, "@as_of", request.AsOf, DbType.DateTimeOffset);
        AddParameter(command, "@limit", request.Limit, DbType.Int32);
        AddParameter(command, "@offset", request.Offset, DbType.Int32);

        var hits = new List<SearchHit>(request.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetDouble(4),
                null,
                reader.GetString(5)));
        }

        return hits;
    }

    private static void AddParameter(DbCommand command, string name, object? value, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}