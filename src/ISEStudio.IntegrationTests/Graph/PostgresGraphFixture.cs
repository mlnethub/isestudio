using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Documents;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    public async Task<HashSet<(string Table, string Column, string PrincipalTable, string PrincipalColumn)>> GetForeignKeysAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT key_usage.table_name,
                   key_usage.column_name,
                   reference_usage.table_name,
                   reference_usage.column_name
            FROM information_schema.key_column_usage AS key_usage
            JOIN information_schema.constraint_column_usage AS reference_usage
              ON reference_usage.constraint_schema = key_usage.constraint_schema
             AND reference_usage.constraint_name = key_usage.constraint_name
            JOIN information_schema.table_constraints AS constraints
              ON constraints.constraint_schema = key_usage.constraint_schema
             AND constraints.constraint_name = key_usage.constraint_name
             AND constraints.table_name = key_usage.table_name
            WHERE key_usage.table_schema = 'public'
              AND constraints.constraint_type = 'FOREIGN KEY'";

        await using var reader = await command.ExecuteReaderAsync();
        var foreignKeys = new HashSet<(string, string, string, string)>();
        while (await reader.ReadAsync())
        {
            foreignKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return foreignKeys;
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

    public async Task ResetGraphWritesAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM fact_evidence;
            DELETE FROM facts;
            DELETE FROM auditevent WHERE ""Action"" LIKE 'graph.fact.%';
            DELETE FROM graph_entities WHERE knowledge_system_id = @ks
              AND id NOT IN (@subject, @object);";
        command.Parameters.AddWithValue("ks", KnowledgeSystemId);
        command.Parameters.AddWithValue("subject", SubjectEntityId);
        command.Parameters.AddWithValue("object", ObjectEntityId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Guid> CreateTraversalFixtureAsync(int factCount)
    {
        if (factCount < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(factCount));
        }

        var chainEntityIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var chain = new[] { SubjectEntityId, chainEntityIds[0], chainEntityIds[1], chainEntityIds[2], chainEntityIds[3], ObjectEntityId };

        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var entities = connection.CreateCommand())
        {
            entities.Transaction = transaction;
            entities.CommandText = @"
                INSERT INTO graph_entities (id, knowledge_system_id, entity_type_id, label, description)
                SELECT value, @ks, NULL, 'Traversal entity', 'Performance fixture'
                FROM unnest(@ids) AS value;";
            entities.Parameters.AddWithValue("ks", KnowledgeSystemId);
            entities.Parameters.AddWithValue("ids", chainEntityIds);
            await entities.ExecuteNonQueryAsync();
        }

        await using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = @"
                INSERT INTO facts (
                    id, knowledge_system_id, subject_entity_id, predicate_id,
                      object_value, confidence, recorded_at)
                  SELECT gen_random_uuid(), @ks, @subject, @predicate,
                      '""padding""'::jsonb, 0.9, now()
                FROM generate_series(1, @padding);

                INSERT INTO facts (
                    id, knowledge_system_id, subject_entity_id, predicate_id,
                    object_entity_id, confidence, recorded_at)
                SELECT gen_random_uuid(), @ks, chain.subject_entity_id, @predicate,
                       chain.object_entity_id, 0.9, now()
                FROM (VALUES
                    (@chain0, @chain1),
                    (@chain1, @chain2),
                    (@chain2, @chain3),
                    (@chain3, @chain4),
                    (@chain4, @chain5)) AS chain(subject_entity_id, object_entity_id);";
            facts.Parameters.AddWithValue("ks", KnowledgeSystemId);
            facts.Parameters.AddWithValue("subject", SubjectEntityId);
            facts.Parameters.AddWithValue("predicate", PredicateId);
            facts.Parameters.AddWithValue("object", ObjectEntityId);
            facts.Parameters.AddWithValue("padding", factCount - 5);
            for (var index = 0; index < chain.Length; index++)
            {
                facts.Parameters.AddWithValue($"chain{index}", chain[index]);
            }

            await facts.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return SubjectEntityId;
    }

    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(_container.GetConnectionString())
                .Options;
            return new PgDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddScoped<DocumentVersionStore>();
        services.AddSingleton<Chunker>(_ => new Chunker(size: 20, overlap: 0));
        services.AddScoped<PlainTextIngestionService>();
        services.AddScoped<PlainTextIngestionJobProcessor>();
        services.AddScoped<DocumentIngestionJobProcessor>();
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(
            Path.Combine(Path.GetTempPath(), "isestudio-stage4", Guid.NewGuid().ToString("N"))));
        configure?.Invoke(services);
        services.AddGraphStore();
        return services.BuildServiceProvider();
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

    private sealed class PgDbContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;

        public PgDbContextFactory(DbContextOptions<ISEStudioDbContext> options)
        {
            _options = options;
        }

        public ISEStudioDbContext CreateDbContext() => new(_options);
    }
}