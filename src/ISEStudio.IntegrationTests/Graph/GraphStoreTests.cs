using ISEStudio.Graph;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Infrastructure.Persistence;
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

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.RecordFactAsync(command, CancellationToken.None));

        await using var verify = CreateDbContext();
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.FactEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents
            .Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .ToListAsync());
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
        await db.SaveChangesAsync();
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