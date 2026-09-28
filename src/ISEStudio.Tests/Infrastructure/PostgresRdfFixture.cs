using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ISEStudio.Tests.Infrastructure;

public class PostgresRdfFixture : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private ISEStudioDbContext _db = null!;

    public Guid KnowledgeSystemId { get; } = Guid.NewGuid();

    public ISEStudioDbContext Db => _db;

    public IRdfStatementRepository Statements { get; private set; } = null!;

    public PostgresRdfGraphStore TBox { get; private set; } = null!;

    public PostgresRdfGraphStore ABox { get; private set; } = null!;

    public PostgresRdfGraphStore Vocabulary { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        _db = new ISEStudioDbContext(options);
        await _db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        _db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = KnowledgeSystemId,
            PublicId = $"rdf-test-{KnowledgeSystemId:N}",
            Name = "RDF test knowledge system",
            GraphIri = $"http://goodcrew.local/ks/test/{KnowledgeSystemId:N}",
            BaseIri = $"http://goodcrew.local/ks/test/{KnowledgeSystemId:N}/onto#",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();
        Statements = new PostgresRdfStatementRepository(_db);
        TBox = new PostgresRdfGraphStore(Statements, KnowledgeSystemId, RdfLayer.TBox.ToString());
        ABox = new PostgresRdfGraphStore(Statements, KnowledgeSystemId, RdfLayer.ABox.ToString());
        Vocabulary = new PostgresRdfGraphStore(Statements, KnowledgeSystemId, RdfLayer.Vocabulary.ToString());
    }

    public async Task ResetAsync()
    {
        _db.ChangeTracker.Clear();
        await _db.WorkspaceStatements.Where(_ => true).ExecuteDeleteAsync();
        await _db.ReleaseStatements.Where(_ => true).ExecuteDeleteAsync();
        await _db.ReleaseDeployments.Where(_ => true).ExecuteDeleteAsync();
        await _db.ReleaseStatementProvenances.Where(_ => true).ExecuteDeleteAsync();
        await _db.ExportJobs.Where(_ => true).ExecuteDeleteAsync();
        await _db.OntologyReleases.Where(_ => true).ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }
}