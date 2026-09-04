using ISEStudio.Graph;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ISEStudio.IntegrationTests.Graph;

public sealed class GraphStoreTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public GraphStoreTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Record_fact_persists_fact_and_evidence_in_one_transaction()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 2);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var fact = await store.RecordFactAsync(command, CancellationToken.None);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.Facts.CountAsync(item => item.Id == fact.Id));
        Assert.Equal(2, await verify.FactEvidence.CountAsync(item => item.FactId == fact.Id));

        var audit = await verify.AuditEvents.SingleAsync(item =>
            item.KnowledgeSystemId == _fixture.KnowledgeSystemId &&
            item.Action == "graph.fact.recorded");

        Assert.Equal("graph.fact.recorded", audit.Action);
        Assert.NotNull(audit.Detail);
        Assert.Equal(fact.Id, audit.Detail!.RootElement.GetProperty("factId").GetGuid());
        Assert.Equal(_fixture.KnowledgeSystemId, audit.Detail.RootElement.GetProperty("knowledgeSystemId").GetGuid());
        Assert.Equal(2, audit.Detail.RootElement.GetProperty("evidenceCount").GetInt32());
    }

    [Fact]
    public async Task Record_fact_rolls_back_when_evidence_references_unknown_chunk()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1) with
        {
            Evidence = [new FactEvidenceInput(Guid.NewGuid(), "quote", "predicate")]
        };

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordFactAsync(command, CancellationToken.None));

        Assert.Contains("chunk", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = CreateDbContext();
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.FactEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents
            .Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .ToListAsync());
    }

    [Fact]
    public async Task Record_fact_rejects_evidence_chunk_from_different_knowledge_system()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var foreignChunkId = await CreateForeignChunkAsync();
        var command = NewEntityObjectFact(evidenceCount: 1) with
        {
            Evidence = [new FactEvidenceInput(foreignChunkId, "quote", "predicate")]
        };

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordFactAsync(command, CancellationToken.None));

        Assert.Contains("does not belong to knowledge system", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = CreateDbContext();
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.FactEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents
            .Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .ToListAsync());
    }

    [Fact]
    public async Task Invalidate_fact_marks_live_fact_in_same_knowledge_system_and_writes_audit()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var fact = await store.RecordFactAsync(command, CancellationToken.None);
        var invalidatedAt = DateTimeOffset.UtcNow;

        await store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, invalidatedAt, CancellationToken.None);

        await using var verify = CreateDbContext();
        var persistedFact = await verify.Facts.SingleAsync(item => item.Id == fact.Id);
        Assert.Equal(TruncateToPostgresPrecision(invalidatedAt), persistedFact.InvalidatedAt);

        var audit = await verify.AuditEvents.SingleAsync(item =>
            item.KnowledgeSystemId == _fixture.KnowledgeSystemId &&
            item.Action == "graph.fact.invalidated");

        Assert.Equal("graph.fact.invalidated", audit.Action);
        Assert.NotNull(audit.Detail);
        Assert.Equal(fact.Id, audit.Detail!.RootElement.GetProperty("factId").GetGuid());
        Assert.Equal(_fixture.KnowledgeSystemId, audit.Detail.RootElement.GetProperty("knowledgeSystemId").GetGuid());
    }

    [Fact]
    public async Task Invalidate_fact_throws_when_fact_is_already_invalidated_or_in_another_knowledge_system()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var fact = await store.RecordFactAsync(command, CancellationToken.None);
        await store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var otherKnowledgeSystemId = await CreateKnowledgeSystemAsync("graph-store-other-ks");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.InvalidateFactAsync(otherKnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None));
    }

    private RecordFactCommand NewEntityObjectFact(int evidenceCount)
    {
        return new RecordFactCommand(
            _fixture.KnowledgeSystemId,
            _fixture.SubjectEntityId,
            _fixture.PredicateId,
            GraphObjectKind.Entity,
            _fixture.ObjectEntityId,
            null,
            0.9m,
            null,
            null,
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, evidenceCount)
                .Select(_ => new FactEvidenceInput(_fixture.ChunkId, "fixture quote", "supports"))
                .ToArray(),
            null);
    }

    private async Task ResetGraphWritesAsync()
    {
        await using var db = CreateDbContext();
        db.FactEvidence.RemoveRange(db.FactEvidence);
        db.Facts.RemoveRange(db.Facts);
        db.AuditEvents.RemoveRange(db.AuditEvents.Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId));
        db.Chunks.RemoveRange(db.Chunks.Where(item => item.Id != _fixture.ChunkId));
        db.Documents.RemoveRange(db.Documents.Where(item => item.KnowledgeSystemId != _fixture.KnowledgeSystemId));
        db.KnowledgeSystems.RemoveRange(db.KnowledgeSystems.Where(item => item.Id != _fixture.KnowledgeSystemId));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateForeignChunkAsync()
    {
        var otherKnowledgeSystemId = await CreateKnowledgeSystemAsync("graph-store-foreign-chunk");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        db.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            KnowledgeSystemId = otherKnowledgeSystemId,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            OriginalFilename = "foreign.txt",
            Folder = "/",
            Ext = "txt",
            SizeBytes = 14,
            StoragePath = $"foreign/{documentId:N}",
            UploadedAt = now,
        });
        db.Chunks.Add(new ChunkEntity
        {
            Id = chunkId,
            DocumentId = documentId,
            Idx = 0,
            Text = "foreign chunk",
            CharStart = 0,
            CharEnd = 13,
            TokenEstimate = 2,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        return chunkId;
    }

    private async Task<Guid> CreateKnowledgeSystemAsync(string publicId)
    {
        var knowledgeSystemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = knowledgeSystemId,
            PublicId = publicId,
            Name = publicId,
            Description = "Fixture knowledge system",
            GraphIri = $"https://example.test/{publicId}",
            BaseIri = $"https://example.test/{publicId}#",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return knowledgeSystemId;
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % 10), value.Offset);
    }

    private ISEStudioDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(GetConnectionString())
            .Options;
        return new ISEStudioDbContext(options);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(GetConnectionString())
                .Options;
            return new PgDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddGraphStore();
        return services.BuildServiceProvider();
    }

    private string GetConnectionString()
    {
        using var connection = _fixture.OpenConnectionAsync().GetAwaiter().GetResult();
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
        {
            Password = "postgres",
            PersistSecurityInfo = true,
        };

        return builder.ConnectionString;
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