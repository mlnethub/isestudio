using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Graph;

public sealed class PostgresGraphFixture : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private ISEStudioDbContext _db = null!;
    private bool _graphReferencesSeeded;

    public Guid KnowledgeSystemId { get; } = Guid.NewGuid();

    public Guid SubjectEntityId { get; } = Guid.NewGuid();

    public Guid PredicateId { get; } = Guid.NewGuid();

    public Guid ObjectEntityId { get; } = Guid.NewGuid();

    public Guid ChunkId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        _db = new ISEStudioDbContext(options);
        await _db.Database.MigrateAsync();
        await SeedWorkspaceRowsAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory'";

        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async Task<HashSet<(string Column, string DataType)>> GetColumnsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table";
        command.Parameters.AddWithValue("table", table);

        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<(string, string)>();
        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    public async Task<Dictionary<string, string>> GetIndexDefinitionsAsync(params string[] tables)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = ANY(@tables)";
        command.Parameters.AddWithValue("tables", tables);

        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            definitions[reader.GetString(0)] = reader.GetString(1);
        }

        return definitions;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public async Task SeedGraphReferencesAsync()
    {
        if (_graphReferencesSeeded)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO entity_types (id, knowledge_system_id, key, label, description)
            VALUES (@entity_type, @ks, 'pump', 'Pump', 'Seed entity type');

            INSERT INTO relation_types (id, knowledge_system_id, key, label, description)
            VALUES (@predicate, @ks, 'connected_to', 'Connected To', 'Seed relation type');

            INSERT INTO graph_entities (id, knowledge_system_id, entity_type_id, label, description)
            VALUES (@subject, @ks, @entity_type, 'Pump A', 'Seed subject entity'),
                   (@object, @ks, @entity_type, 'Pump B', 'Seed object entity');
        ";

        command.Parameters.AddWithValue("entity_type", Guid.NewGuid());
        command.Parameters.AddWithValue("predicate", PredicateId);
        command.Parameters.AddWithValue("ks", KnowledgeSystemId);
        command.Parameters.AddWithValue("subject", SubjectEntityId);
        command.Parameters.AddWithValue("object", ObjectEntityId);

        await command.ExecuteNonQueryAsync();
        _graphReferencesSeeded = true;
    }

    private async Task SeedWorkspaceRowsAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = KnowledgeSystemId,
            PublicId = "graph-schema-test",
            Name = "Graph Schema Test",
            Description = "Fixture knowledge system",
            GraphIri = "https://example.test/graph",
            BaseIri = "https://example.test/base#",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var documentId = Guid.NewGuid();
        _db.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            KnowledgeSystemId = KnowledgeSystemId,
            Sha256 = new string('a', 64),
            OriginalFilename = "fixture.txt",
            Folder = "/",
            Ext = "txt",
            SizeBytes = 12,
            StoragePath = "aa/bb/fixture",
            UploadedAt = now,
        });

        _db.Chunks.Add(new ChunkEntity
        {
            Id = ChunkId,
            DocumentId = documentId,
            Idx = 0,
            Text = "fixture chunk",
            CharStart = 0,
            CharEnd = 12,
            TokenEstimate = 2,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync();
    }
}