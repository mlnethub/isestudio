using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ISEStudio.Documents;
using ISEStudio.Graph;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Search;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Benchmarks;

public sealed class BenchmarkFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private ISEStudioDbContext _db = null!;

    public BenchmarkInput Input { get; private set; } = null!;
    public Guid KnowledgeSystemId { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid RootEntityId { get; private set; }
    public Guid LeafEntityId { get; private set; }
    public Guid PredicateId { get; private set; }
    public Guid SearchDocumentId { get; private set; }
    public Guid IngestionDocumentId { get; private set; }

    public async Task InitializeAsync()
    {
        Input = await BenchmarkInput.LoadAsync();
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        _db = new ISEStudioDbContext(options);
        await _db.Database.MigrateAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(_container.GetConnectionString())
                .Options;
            return new BenchmarkDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddScoped<DocumentVersionStore>();
        services.AddSingleton<Chunker>(_ => new Chunker(size: 20, overlap: 0));
        services.AddScoped<PlainTextIngestionService>();
        services.AddGraphStore();
        return services.BuildServiceProvider();
    }

    public async Task<string> RunTraversalAsync()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                KnowledgeSystemId,
                RootEntityId,
                Input.QueryDepth,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                false),
            CancellationToken.None);

        return StableHash($"{result.EntityIds.Count}|{result.Facts.Count}|{result.Entities.Count}");
    }

    public async Task<string> RunSearchAsync()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var index = new PostgresSearchIndex(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>());
        var hits = await index.SearchAsync(
            new ISEStudio.Application.Search.SearchRequest(
                KnowledgeSystemId,
                "pump",
                Limit: Math.Min(Input.ChunkCount, 100),
                ActorId: OwnerId),
            CancellationToken.None);

        return StableHash(string.Join("\n", hits.Select(hit => $"{hit.Text}|{hit.SourceSha256}")));
    }

    public async Task<string> RunIngestionAsync()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<PlainTextIngestionService>();
        var content = string.Join(
            "\n\n",
            Enumerable.Range(0, Input.ChunkCount).Select(index => $"ingestion chunk {index}"));
        var version = await ingestion.IngestAsync(
            KnowledgeSystemId,
            IngestionDocumentId,
            content,
            CancellationToken.None);

        return StableHash($"{version.ContentSha256}|{version.ChunkCount}");
    }

    public async Task<string> RunConcurrentGraphWritesAsync()
    {
        var writes = Enumerable.Range(0, Input.ConcurrentWriters).Select(async index =>
        {
            await using var services = BuildServices();
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
            await store.RecordFactAsync(
                new RecordFactCommand(
                    KnowledgeSystemId,
                    RootEntityId,
                    PredicateId,
                    GraphObjectKind.Entity,
                    LeafEntityId,
                    null,
                    0.9m,
                    null,
                    null,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    [new FactEvidenceInput(
                        await FindEvidenceChunkAsync(),
                        $"concurrent write {index}",
                        "benchmark")],
                    null),
                CancellationToken.None);
        });

        await Task.WhenAll(writes);
        return StableHash($"{Input.ConcurrentWriters}|{Input.ConcurrentWriters}");
    }

    public async Task<BenchmarkMetric> MeasureAsync(
        string name,
        Func<Task<string>> operation,
        int sampleCount = 3)
    {
        _ = await operation();
        var timings = new List<double>(sampleCount);
        var hashes = new List<string>(sampleCount);
        var errors = 0;

        for (var index = 0; index < sampleCount; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                hashes.Add(await operation());
            }
            catch
            {
                errors++;
            }

            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var resultHash = errors == 0 && hashes.Distinct(StringComparer.Ordinal).Count() == 1
            ? hashes[0]
            : StableHash(string.Join("|", hashes.Append($"errors:{errors}")));
        var ordered = timings.OrderBy(value => value).ToArray();
        var metric = new BenchmarkMetric(
            name,
            ordered[ordered.Length / 2],
            Percentile(ordered, 0.95),
            sampleCount / Math.Max(timings.Sum() / 1000d, 0.000001d),
            errors,
            resultHash);

        await BenchmarkOutput.WriteAsync(metric);
        VerifyExpectedHash(name, resultHash);
        Assert.Equal(0, errors);
        Assert.All(hashes, hash => Assert.Equal(resultHash, hash));
        return metric;
    }

    private void VerifyExpectedHash(string name, string actualHash)
    {
        if (Environment.GetEnvironmentVariable("ISESTUDIO_BENCHMARK_RECORD") == "1")
        {
            return;
        }

        var hasExpected = Input.ExpectedResultHashes.TryGetValue(name, out var expected);
        Assert.True(hasExpected && !string.IsNullOrWhiteSpace(expected), $"Missing expected hash for {name}; run with -Record.");
        Assert.Equal(expected, actualHash);
    }

    private async Task<Guid> FindEvidenceChunkAsync()
    {
        await using var db = new ISEStudioDbContext(new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options);
        return await db.Chunks.Select(chunk => chunk.Id).FirstAsync();
    }

    private async Task SeedAsync()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        KnowledgeSystemId = StableGuid("knowledge-system");
        OwnerId = StableGuid("owner");
        RootEntityId = StableGuid("root");
        LeafEntityId = StableGuid("leaf");
        PredicateId = StableGuid("predicate");
        SearchDocumentId = StableGuid("search-document");
        IngestionDocumentId = StableGuid("ingestion-document");

        var entityTypeId = StableGuid("entity-type");
        var chainIds = Enumerable.Range(0, Input.QueryDepth)
            .Select(index => StableGuid($"chain-{index}"))
            .ToArray();
        var chain = new[] { RootEntityId }.Concat(chainIds).Append(LeafEntityId).ToArray();
        var searchVersionId = StableGuid("search-version");

        _db.Users.Add(new UserEntity
        {
            Id = OwnerId,
            Username = "benchmark-owner",
            PasswordHash = "test",
            CreatedAt = now,
        });
        _db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = KnowledgeSystemId,
            PublicId = "benchmark-ks",
            Name = "Benchmark KS",
            OwnerId = OwnerId,
            GraphIri = "https://example.test/benchmark",
            BaseIri = "https://example.test/benchmark#",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.EntityTypes.Add(new EntityTypeEntity
        {
            Id = entityTypeId,
            KnowledgeSystemId = KnowledgeSystemId,
            Key = "benchmark-entity",
            Label = "Benchmark entity",
        });
        _db.RelationTypes.Add(new RelationTypeEntity
        {
            Id = PredicateId,
            KnowledgeSystemId = KnowledgeSystemId,
            Key = "connected-to",
            Label = "Connected to",
        });
        _db.GraphEntities.AddRange(chain.Select((id, index) => new GraphEntityEntity
        {
            Id = id,
            KnowledgeSystemId = KnowledgeSystemId,
            EntityTypeId = entityTypeId,
            Label = $"Benchmark entity {index}",
        }));

        _db.Documents.AddRange(
            new DocumentEntity
            {
                Id = SearchDocumentId,
                KnowledgeSystemId = KnowledgeSystemId,
                Sha256 = new string('1', 64),
                OriginalFilename = "benchmark-search.txt",
                Ext = "txt",
                StoragePath = "11/search",
                UploadedAt = now,
                ParseStatus = "parsed",
            },
            new DocumentEntity
            {
                Id = IngestionDocumentId,
                KnowledgeSystemId = KnowledgeSystemId,
                Sha256 = new string('2', 64),
                OriginalFilename = "benchmark-ingestion.txt",
                Ext = "txt",
                StoragePath = "22/ingestion",
                UploadedAt = now,
            });
        _db.Chunks.Add(new ChunkEntity
        {
            Id = StableGuid("evidence-chunk"),
            DocumentId = SearchDocumentId,
            Idx = 0,
            Text = "benchmark evidence",
            CharStart = 0,
            CharEnd = 18,
            TokenEstimate = 2,
            CreatedAt = now,
        });
        _db.DocumentVersions.Add(new DocumentVersionEntity
        {
            Id = searchVersionId,
            KnowledgeSystemId = KnowledgeSystemId,
            DocumentId = SearchDocumentId,
            ContentSha256 = new string('3', 64),
            ChunkCount = Input.ChunkCount,
            CreatedAt = now,
        });
        _db.DocumentVersionChunks.AddRange(Enumerable.Range(0, Input.ChunkCount).Select(index =>
            new DocumentVersionChunkEntity
            {
                Id = StableGuid($"search-chunk-{index}"),
                DocumentVersionId = searchVersionId,
                Idx = index,
                Text = $"pump benchmark procedure {index}",
                CharStart = index * 24,
                CharEnd = index * 24 + 24,
                TokenEstimate = 4,
            }));
        _db.Facts.AddRange(Enumerable.Range(0, Input.FactCount).Select(index =>
            index < chain.Length - 1
                ? new FactEntity
                {
                    Id = StableGuid($"chain-fact-{index}"),
                    KnowledgeSystemId = KnowledgeSystemId,
                    SubjectEntityId = chain[index],
                    PredicateId = PredicateId,
                    ObjectEntityId = chain[index + 1],
                    Confidence = 0.9m,
                    RecordedAt = now,
                }
                : new FactEntity
                {
                    Id = StableGuid($"padding-fact-{index}"),
                    KnowledgeSystemId = KnowledgeSystemId,
                    SubjectEntityId = RootEntityId,
                    PredicateId = PredicateId,
                    ObjectValue = "{\"padding\":true}",
                    Confidence = 0.9m,
                    RecordedAt = now,
                }));

        await _db.SaveChangesAsync();
    }

    private static double Percentile(double[] values, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1);
        return values[index];
    }

    public static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes[..16]);
    }

    public static string StableHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class BenchmarkDbContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;

        public BenchmarkDbContextFactory(DbContextOptions<ISEStudioDbContext> options) => _options = options;

        public ISEStudioDbContext CreateDbContext() => new(_options);
    }
}

public sealed record BenchmarkInput(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("fact_count")] int FactCount,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("concurrent_writers")] int ConcurrentWriters,
    [property: JsonPropertyName("query_depth")] int QueryDepth,
    [property: JsonPropertyName("expected_result_hashes")] Dictionary<string, string> ExpectedResultHashes)
{
    public static async Task<BenchmarkInput> LoadAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Benchmarks", "Fixtures", "graph-search-ingestion.v1.json");
            if (File.Exists(candidate))
            {
                await using var stream = File.OpenRead(candidate);
                return (await JsonSerializer.DeserializeAsync<BenchmarkInput>(stream))
                    ?? throw new InvalidDataException("The benchmark fixture is empty.");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The versioned benchmark fixture was not found.");
    }
}

public sealed record BenchmarkMetric(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("p50_ms")] double P50Ms,
    [property: JsonPropertyName("p95_ms")] double P95Ms,
    [property: JsonPropertyName("throughput_per_second")] double ThroughputPerSecond,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("result_hash")] string ResultHash);

public static class BenchmarkOutput
{
    public static async Task WriteAsync(BenchmarkMetric metric)
    {
        var directory = Environment.GetEnvironmentVariable("ISESTUDIO_BENCHMARK_OUTPUT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "isestudio-benchmarks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{metric.Name}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metric, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}