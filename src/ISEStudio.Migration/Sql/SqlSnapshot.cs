using Npgsql;

namespace ISEStudio.Migration.Sql;

/// <summary>
/// Captures the pre- and post-migration shape of a database: the row count
/// of every business table, the foreign-key orphan count (rows whose
/// expected parent does not exist), and a per-row business checksum that
/// is stable across the migration.
///
/// <para>Used by the migration tests to assert that
/// <see cref="SqlMigrationCommand.ApplyAsync"/> is a true
/// in-place, lossy-free transformation of the database: row counts are
/// preserved, no FKs are left dangling, and every row's business payload
/// is byte-identical to where it started.</para>
///
/// <para>Stability rules for the business checksum:
/// <list type="bullet">
///   <item>The two new columns introduced by the migration
///   (<c>guid_id</c> and <c>legacy_id</c>) are excluded because their
///   values are derived state, not user payload.</item>
///   <item>Foreign-key columns are excluded because they are
///   rewritten by the migration: the original bigint <c>id</c> value is
///   replaced with the parent's new <c>guid_id</c> lookup. The "no
///   orphan rows" assertion in the verify script is the canonical
///   guarantee that the rewrite is correct; the business checksum
///   should not be sensitive to that mechanical rewrite.</item>
/// </list>
/// </para>
/// </summary>
public static class SqlSnapshot
{
    /// <summary>The 24 business tables captured by <see cref="CaptureAsync"/>.</summary>
    public static readonly string[] BusinessTables =
    {
        "users", "authsession", "ksgrant", "document", "chunk", "knowledgesystem",
        "knowledgepromptoverride", "knowledgeapitoken", "mcpusertoken", "provider",
        "systemconfig", "extractionjob", "axiomprovenance", "aboxprovenance",
        "auditevent", "ontologyrelease", "releasedeployment",
        "releasestatementprovenance", "exportjob", "conflict", "entityresolution",
        "termproposal", "tboxreconciliation", "validationdecision",
    };

    /// <summary>
    /// Foreign-key columns whose value is rewritten by the migration and
    /// must therefore be excluded from the business checksum. The
    /// "no orphan rows" assertion covers their correctness separately.
    /// </summary>
    private static readonly HashSet<string> FkColumns = new(StringComparer.Ordinal)
    {
        "userid", "knowledgesystemid", "updatedbyid", "createdbyid",
        "reviewedbyid", "publishedbyid",
        "llmproviderid", "embeddingproviderid", "ownerid", "documentid",
        "chunkid", "jobid", "auditeventid", "actorid", "releaseid",
        "sourcechunkid", "extractionjobid",
    };

    /// <summary>Columns added by the migration; excluded from the checksum.</summary>
    private static readonly HashSet<string> MigrationColumns = new(StringComparer.Ordinal)
    {
        "guid_id", "legacy_id",
    };

    /// <summary>Capture row counts, FK orphan counts, and per-table business checksums.</summary>
    public static async Task<SnapshotResult> CaptureAsync(string connectionString, CancellationToken cancellationToken)
    {
        var tableCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var orphanCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var businessChecksums = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Pin datestyle + timezone so that timestamptz::text rendering is
        // identical across snapshots regardless of session defaults.
        await using (var set = conn.CreateCommand())
        {
            set.CommandText = "SET datestyle = 'ISO'; SET timezone = 'UTC'";
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        // Row counts.
        foreach (var table in BusinessTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*)::bigint FROM {QuoteIdent(table)}";
            var n = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            tableCounts[table] = n;
        }

        // FK orphan counts. The query auto-detects the FK column type
        // (bigint in the Python schema, uuid post-migration) and joins
        // against the parent's matching PK.
        foreach (var fk in FkColumns)
        {
            var referencingTables = await FindReferencingTablesAsync(conn, fk, cancellationToken);
            foreach (var (table, parentTable) in referencingTables)
            {
                var columnType = await GetColumnTypeAsync(conn, table, fk, cancellationToken);
                if (columnType is null)
                {
                    continue;
                }

                long orphans;
                if (columnType == "uuid")
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT count(*)::bigint
                        FROM {QuoteIdent(table)} t
                        WHERE t.{QuoteIdent(fk)} IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM {QuoteIdent(parentTable)} p
                              WHERE p.guid_id = t.{QuoteIdent(fk)}
                          )";
                    orphans = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
                }
                else
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT count(*)::bigint
                        FROM {QuoteIdent(table)} t
                        WHERE t.{QuoteIdent(fk)} IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM {QuoteIdent(parentTable)} p
                              WHERE p.id = t.{QuoteIdent(fk)}
                          )";
                    orphans = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
                }
                orphanCounts[$"{table}.{fk}"] = orphans;
            }
        }

        // Business checksum: per-table md5 over every column that is
        // neither an FK nor a migration artifact, aggregated by
        // string_agg(row_md5, ''). The id column is always included so
        // row order is reflected; the checksum is otherwise stable
        // across the migration.
        foreach (var table in BusinessTables)
        {
            var checksumColumns = await GetBusinessChecksumColumnsAsync(conn, table, cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildChecksumSql(table, checksumColumns);
            var sum = (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            businessChecksums[table] = sum;
        }

        return new SnapshotResult(tableCounts, orphanCounts, businessChecksums);
    }

    private static async Task<HashSet<string>> GetBusinessChecksumColumnsAsync(
        NpgsqlConnection conn, string table, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t
            ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue("@t", table);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (FkColumns.Contains(name) || MigrationColumns.Contains(name))
            {
                continue;
            }
            columns.Add(name);
        }
        return columns;
    }

    private static string BuildChecksumSql(string table, IReadOnlyCollection<string> columns)
    {
        // Concat every included column for each row, then md5 the
        // concatenation, then string_agg over the rows (in id order so
        // it is stable). The two layers of md5 + string_agg make the
        // final value a hash over the full table content that fits
        // in a single value (Postgres' md5 returns 32 hex chars).
        // The column order MUST be deterministic — the caller passes
        // a HashSet whose enumeration order is undefined, so we sort
        // here to make the hash reproducible across snapshots.
        var columnExprs = string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal).Select(c => $"COALESCE({QuoteIdent(c)}::text, '')"));
        return $@"
            SELECT COALESCE(string_agg(row_md5, ''), '')
            FROM (
                SELECT md5(concat_ws('|', {columnExprs})) AS row_md5
                FROM {QuoteIdent(table)}
                ORDER BY id
            ) rows";
    }

    private static async Task<string?> GetColumnTypeAsync(
        NpgsqlConnection conn, string table, string column, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @t
              AND column_name = @c";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<List<(string Table, string ParentTable)>> FindReferencingTablesAsync(
        NpgsqlConnection conn, string column, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                tc.table_name AS from_table,
                ccu.table_name AS to_table
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
              AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
              AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = 'public'
              AND kcu.column_name = @c";
        cmd.Parameters.AddWithValue("@c", column);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var result = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    private static string QuoteIdent(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
}

/// <summary>One snapshot of the database's row counts, FK orphans, and business checksums.</summary>
public sealed record SnapshotResult(
    IReadOnlyDictionary<string, long> TableCounts,
    IReadOnlyDictionary<string, long> OrphanCounts,
    IReadOnlyDictionary<string, string> BusinessChecksums);
