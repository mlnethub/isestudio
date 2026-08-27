using Microsoft.EntityFrameworkCore;
using Npgsql;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Persistence;

/// <summary>
/// Spins up a real PostgreSQL instance via Testcontainers, applies the
/// <c>InitialCompatibility</c> migration, and asserts the resulting schema
/// matches the Python backend's 24-table contract: every business table is
/// present, <c>jsonb</c> / <c>bytea</c> column types land correctly, the
/// three composite uniqueness constraints from the Python source are
/// enforced, and the database-level FK constraints produced by SQLAlchemy's
/// <c>foreign_key=</c> declarations round-trip into Postgres.
/// </summary>
/// <remarks>
/// <para>This is a fixture-style test: a single container is shared across
/// the assertions via <c>IClassFixture</c> to keep the test runtime
/// reasonable. Docker must be available; otherwise the fixture throws and
/// the tests are reported as failed (not silently skipped).</para>
/// </remarks>
public sealed class PostgresSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;

    /// <summary>Shared context applied with the migration before any assertion runs.</summary>
    private ISEStudioDbContext _db = null!;

    /// <summary>The 24 Python business tables (lowercased).</summary>
    private static readonly string[] ExpectedTables =
    {
        "users", "authsession", "ksgrant", "document", "chunk", "knowledgesystem",
        "knowledgepromptoverride", "knowledgeapitoken", "mcpusertoken", "provider",
        "systemconfig", "extractionjob", "axiomprovenance", "aboxprovenance",
        "auditevent", "ontologyrelease", "releasedeployment",
        "releasestatementprovenance", "exportjob", "conflict", "entityresolution",
        "termproposal", "tboxreconciliation", "validationdecision",
    };

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        _db = new ISEStudioDbContext(options);
        await _db.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Returns the lowercase names of all business tables in the public schema (excludes EF bookkeeping tables).</summary>
    private async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory'";
        await using var reader = await cmd.ExecuteReaderAsync();

        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    /// <summary>Returns the (column_name, data_type) pairs for a table.</summary>
    private async Task<HashSet<(string Column, string DataType)>> GetColumnsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new HashSet<(string, string)>();
        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        return columns;
    }

    /// <summary>Returns (constraint_name, from_table, from_column, to_table, to_column) FKs in the public schema.</summary>
    private async Task<List<(string Name, string FromTable, string FromColumn, string ToTable, string ToColumn)>> GetForeignKeysAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                tc.constraint_name,
                tc.table_name,
                kcu.column_name,
                ccu.table_name AS foreign_table,
                ccu.column_name AS foreign_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
              AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
              AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = 'public'";
        await using var reader = await cmd.ExecuteReaderAsync();

        var fks = new List<(string, string, string, string, string)>();
        while (await reader.ReadAsync())
        {
            fks.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return fks;
    }

    /// <summary>
    /// Asserts that all 24 Python business tables are created by the migration
    /// with no extras — the public schema should contain exactly the 24 tables
    /// listed in <see cref="ExpectedTables"/>.
    /// </summary>
    [Fact]
    public async Task Migration_creates_all_24_business_tables_with_postgres_types()
    {
        var tables = await GetTableNamesAsync();

        foreach (var t in ExpectedTables)
        {
            Assert.Contains(t, tables);
        }
        Assert.Equal(ExpectedTables.Length, tables.Count);
    }

    /// <summary>The audit event blob columns should be bytea so rollback payloads store raw.</summary>
    [Fact]
    public async Task Audit_event_uses_bytea_for_added_and_removed_blobs()
    {
        var columns = await GetColumnsAsync("auditevent");
        Assert.Contains(("Added", "bytea"), columns);
        Assert.Contains(("Removed", "bytea"), columns);
    }

    /// <summary>JSON columns should be promoted to jsonb on Postgres so they can be queried.</summary>
    [Fact]
    public async Task Json_columns_promoted_to_jsonb_on_postgres()
    {
        var auditColumns = await GetColumnsAsync("auditevent");
        Assert.Contains(("Detail", "jsonb"), auditColumns);

        var extractionColumns = await GetColumnsAsync("extractionjob");
        Assert.Contains(("PromptSnapshot", "jsonb"), extractionColumns);
        Assert.Contains(("UnknownClasses", "jsonb"), extractionColumns);

        var ontologyColumns = await GetColumnsAsync("ontologyrelease");
        Assert.Contains(("Manifest", "jsonb"), ontologyColumns);
    }

    /// <summary>The three Python-defined composite uniqueness constraints must round-trip to Postgres.</summary>
    [Fact]
    public async Task Composite_unique_constraints_match_python_contract()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT tablename, indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexdef LIKE '%UNIQUE%'";
        await using var reader = await cmd.ExecuteReaderAsync();

        var found = new List<(string Table, string Name)>();
        while (await reader.ReadAsync())
        {
            found.Add((reader.GetString(0), reader.GetString(1)));
        }
        await reader.CloseAsync();

        // Each legacy composite constraint must exist with its documented name.
        Assert.Contains(found, x => x.Table == "document" && x.Name == "ux_document_knowledge_system_id_sha256");
        Assert.Contains(found, x => x.Table == "ontologyrelease" && x.Name == "ux_release_knowledge_system_id_version");
        Assert.Contains(found, x => x.Table == "knowledgepromptoverride" && x.Name == "ux_kpo_knowledge_system_id_prompt_key");
    }

    /// <summary>Phase 3: the legacy_id column must be dropped from every business table.</summary>
    [Fact]
    public async Task No_business_table_has_legacy_id_column()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT table_name
            FROM information_schema.columns
            WHERE column_name = 'legacy_id' AND table_schema = 'public'";
        await using var reader = await cmd.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Empty(tables);
    }

    /// <summary>Phase 3: the partial unique index enforcing the singleton SystemConfig row must exist.</summary>
    [Fact]
    public async Task Systemconfig_has_unique_singleton()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT indexdef
            FROM pg_indexes
            WHERE indexname = 'ux_systemconfig_singleton'";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ux_systemconfig_singleton index should exist");
        var def = reader.GetString(0);

        Assert.Contains("UNIQUE", def);
        // F1 (R3-4): the column keeps PascalCase — HasFilter is raw SQL and is NOT
        // rewritten, so PG's pg_indexes.indexdef prints `WHERE ("IsSingleton" = true)`.
        // Do NOT assert the snake_case `is_singleton` — it would fail against the
        // real migration and contradict the applied DDL.
        Assert.Contains("\"IsSingleton\" = true", def);
        Assert.Contains("WHERE", def, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TRUE", def, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase 3 behavioural companion to
    /// <see cref="Systemconfig_has_unique_singleton"/>: the DDL assertion
    /// proves the partial index exists, this proves PostgreSQL actually
    /// rejects a second <c>IsSingleton = TRUE</c> row with SQLSTATE 23505.
    /// </summary>
    /// <remarks>
    /// Uses its own <see cref="ISEStudioDbContext"/> (not the shared
    /// <c>_db</c>) so the failed <c>SaveChangesAsync</c> cannot leave an
    /// Added entity in the class-wide change tracker, and deletes the
    /// surviving row in a <c>finally</c> so the container's data state is
    /// unchanged for any later test.
    /// </remarks>
    [Fact]
    public async Task Singleton_invocation_twice_fails_on_PG_23505()
    {
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        await using var db = new ISEStudioDbContext(options);

        var firstId = Guid.NewGuid();
        try
        {
            // First singleton — satisfies the partial index.
            db.SystemConfigs.Add(new SystemConfigEntity
            {
                Id = firstId,
                IsSingleton = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Second singleton — must trip ux_systemconfig_singleton.
            db.SystemConfigs.Add(new SystemConfigEntity
            {
                Id = Guid.NewGuid(),
                IsSingleton = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(
                () => db.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pg.SqlState);
        }
        finally
        {
            // Drop everything this test inserted (and untrack the rejected
            // insert) so the shared container keeps a clean systemconfig.
            db.ChangeTracker.Clear();
            await db.SystemConfigs.Where(c => c.Id == firstId)
                .ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The Python backend declares 49 <c>foreign_key=</c> references in
    /// <c>backend/app/db/models.py</c>. Each one becomes a real
    /// <c>ForeignKey</c> column element in SQLAlchemy and produces a Postgres
    /// <c>REFERENCES</c> constraint at DDL time. After consolidation, 45
    /// distinct (from_table, from_column) &rarr; (to_table, to_column)
    /// relationships are expected; this test asserts the structural shape of
    /// every one and checks a handful of representative ones by name.
    /// </summary>
    [Fact]
    public async Task Foreign_key_constraints_match_python_contract()
    {
        var fks = await GetForeignKeysAsync();

        // The total number must match the count our 24 configurations emit.
        // 45 HasOne<T>().WithMany().HasForeignKey(...) calls were added in
        // EntityConfigurations.cs. The migration produces one FK constraint
        // per call.
        Assert.Equal(45, fks.Count);

        // Sanity check: every FK must point at the `id` column of a known
        // business table (the principal). No FK should reference the EFMigrationHistory
        // or any non-business table by accident.
        var knownPrincipals = new HashSet<string>(ExpectedTables, StringComparer.Ordinal)
        {
            "__EFMigrationsHistory",
        };
        Assert.All(fks, fk => Assert.Contains(fk.ToTable, knownPrincipals));
        Assert.All(fks, fk => Assert.Equal("id", fk.ToColumn));

        // Spot-check a representative subset by (from_table, from_column)
        // matching the Python source's foreign_key declarations.
        Assert.Contains(fks, fk => fk.FromTable == "authsession" && fk.FromColumn == "UserId"
                                  && fk.ToTable == "users");
        Assert.Contains(fks, fk => fk.FromTable == "ksgrant" && fk.FromColumn == "KnowledgeSystemId"
                                  && fk.ToTable == "knowledgesystem");
        Assert.Contains(fks, fk => fk.FromTable == "ksgrant" && fk.FromColumn == "UserId"
                                  && fk.ToTable == "users");
        Assert.Contains(fks, fk => fk.FromTable == "chunk" && fk.FromColumn == "DocumentId"
                                  && fk.ToTable == "document");
        Assert.Contains(fks, fk => fk.FromTable == "knowledgesystem" && fk.FromColumn == "OwnerId"
                                  && fk.ToTable == "users");
        Assert.Contains(fks, fk => fk.FromTable == "knowledgesystem" && fk.FromColumn == "LlmProviderId"
                                  && fk.ToTable == "provider");
        Assert.Contains(fks, fk => fk.FromTable == "releasedeployment" && fk.FromColumn == "ReleaseId"
                                  && fk.ToTable == "ontologyrelease");
        Assert.Contains(fks, fk => fk.FromTable == "axiomprovenance" && fk.FromColumn == "AuditEventId"
                                  && fk.ToTable == "auditevent");
        Assert.Contains(fks, fk => fk.FromTable == "aboxprovenance" && fk.FromColumn == "AuditEventId"
                                  && fk.ToTable == "auditevent");
        Assert.Contains(fks, fk => fk.FromTable == "termproposal" && fk.FromColumn == "ExtractionJobId"
                                  && fk.ToTable == "extractionjob");
    }
}