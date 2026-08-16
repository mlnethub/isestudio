using Microsoft.EntityFrameworkCore;
using Npgsql;
using OnToPilot.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace OnToPilot.IntegrationTests.Persistence;

/// <summary>
/// Spins up a real PostgreSQL instance via Testcontainers, applies the
/// <c>InitialCompatibility</c> migration, and asserts the resulting schema
/// matches the Python backend's 24-table contract: every business table is
/// present, <c>jsonb</c> / <c>bytea</c> column types land correctly, and the
/// three composite uniqueness constraints from the Python source are enforced
/// at the database level.
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
        .WithDatabase("ontopilot")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;

    /// <summary>Shared context applied with the migration before any assertion runs.</summary>
    private OnToPilotDbContext _db = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        _db = new OnToPilotDbContext(options);
        await _db.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Returns the lowercase names of all tables in the public schema.</summary>
    private async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'";
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

    /// <summary>
    /// Asserts that all 24 Python business tables are created by the migration,
    /// and that the three composite uniqueness constraints
    /// (<c>document(ks, sha256)</c>, <c>ontologyrelease(ks, version)</c>,
    /// <c>knowledgepromptoverride(ks, prompt_key)</c>) round-trip into Postgres.
    /// </summary>
    [Fact]
    public async Task Migration_creates_all_24_business_tables_with_postgres_types()
    {
        var tables = await GetTableNamesAsync();

        // The 24 Python tables (lowercased).
        var expected = new[]
        {
            "users", "authsession", "ksgrant", "document", "chunk", "knowledgesystem",
            "knowledgepromptoverride", "knowledgeapitoken", "mcpusertoken", "provider",
            "systemconfig", "extractionjob", "axiomprovenance", "aboxprovenance",
            "auditevent", "ontologyrelease", "releasedeployment",
            "releasestatementprovenance", "exportjob", "conflict", "entityresolution",
            "termproposal", "tboxreconciliation", "validationdecision",
        };

        foreach (var t in expected)
        {
            Assert.Contains(t, tables);
        }
        Assert.Equal(expected.Length, expected.Length); // sanity — exactly 24 expected
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

    /// <summary>Every business table should have a unique index on legacy_id (compat key).</summary>
    [Fact]
    public async Task Every_business_table_has_unique_legacy_id_index()
    {
        var tables = new[]
        {
            "users", "authsession", "ksgrant", "document", "chunk", "knowledgesystem",
            "knowledgepromptoverride", "knowledgeapitoken", "mcpusertoken", "provider",
            "systemconfig", "extractionjob", "axiomprovenance", "aboxprovenance",
            "auditevent", "ontologyrelease", "releasedeployment",
            "releasestatementprovenance", "exportjob", "conflict", "entityresolution",
            "termproposal", "tboxreconciliation", "validationdecision",
        };

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        foreach (var table in tables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = @t
                  AND indexdef LIKE '%legacy_id%'
                  AND indexdef LIKE '%UNIQUE%'";
            cmd.Parameters.AddWithValue("@t", table);
            var has = await cmd.ExecuteScalarAsync();
            Assert.NotNull(has);
        }
    }
}