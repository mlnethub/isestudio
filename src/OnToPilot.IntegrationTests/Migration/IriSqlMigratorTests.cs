using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Migration.Iri;
using Testcontainers.PostgreSql;

namespace OnToPilot.IntegrationTests.Migration;

/// <summary>
/// Integration tests for <see cref="IriSqlMigrator"/>. The migrator
/// runs ten <c>UPDATE ... REPLACE(...)</c> statements (six raw IRI
/// columns + four entity-IRI / fact-key columns) so the test container
/// must use the same EF Core schema as production.
/// <list type="bullet">
///   <item>Each test seeds a fresh <see cref="PostgreSqlContainer"/>,
///   applies the EF Core schema with <c>EnsureCreated</c>, and inserts
///   fixture rows that carry legacy-prefix IRIs.</item>
///   <item>The migrator's <see cref="IriSqlOptions.DryRun"/> flag is
///   asserted to be a no-op against the data; the apply run is
///   asserted to rewrite every column.</item>
///   <item>Re-running the migrator is a no-op (the rewritten rows no
///   longer contain the legacy prefix).</item>
/// </list>
/// </summary>
[Trait("Category", "Migration")]
public sealed class IriSqlMigratorTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ontopilot_iri")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private OnToPilotDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new OnToPilotDbContext(options);
    }

    /// <summary>
    /// Apply the EF Core schema (so the migrator's UPDATE statements
    /// have tables to land in) and seed a single KnowledgeSystem
    /// with IRIs that carry the legacy <c>http://ontopilot.local/</c>
/// prefix in every column the migrator rewrites.
/// </summary>
    private async Task SeedAsync()
    {
        await using var db = BuildContext();
        await db.Database.EnsureCreatedAsync();

        var ks = new KnowledgeSystemEntity
        {
            PublicId = "test-ks",
            Name = "test",
            Description = "iri-migration fixture",
            GraphIri = "http://ontopilot.local/ks/1",
            BaseIri = "http://ontopilot.local/ks/1/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dry_run_reports_would_be_affected_rows_without_writing()
    {
        await SeedAsync();

        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);
        var report = await migrator.MigrateAsync(
            new IriSqlOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/",
                DryRun: true),
            CancellationToken.None);

        // knowledge_systems.graph_iri / base_iri both contain the
        // legacy prefix and would be touched.
        Assert.Contains(report.Steps, s =>
            s.Table == "knowledge_systems" && s.Column == "graph_iri" && s.AffectedRows == 1);
        Assert.Contains(report.Steps, s =>
            s.Table == "knowledge_systems" && s.Column == "base_iri" && s.AffectedRows == 1);

        // The data must NOT have been written.
        await using var verify = BuildContext();
        var ks = await verify.KnowledgeSystems.AsNoTracking().SingleAsync();
        Assert.Equal("http://ontopilot.local/ks/1", ks.GraphIri);
        Assert.Equal("http://ontopilot.local/ks/1/onto#", ks.BaseIri);
    }

    [Fact]
    public async Task Apply_rewrites_every_iri_bearing_column_and_preserves_rows()
    {
        await SeedAsync();

        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);
        var report = await migrator.MigrateAsync(
            new IriSqlOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/"),
            CancellationToken.None);

        // Every step reports the row count we touched.
        Assert.NotEmpty(report.Steps);
        Assert.True(report.TotalRowsChanged > 0);

        // Verify the data was actually rewritten.
        await using var verify = BuildContext();
        var ks = await verify.KnowledgeSystems.AsNoTracking().SingleAsync();
        Assert.Equal("http://goodcrew.local/ks/1", ks.GraphIri);
        Assert.Equal("http://goodcrew.local/ks/1/onto#", ks.BaseIri);
        Assert.Equal("test-ks", ks.PublicId);  // non-IRI columns untouched
    }

    [Fact]
    public async Task Apply_is_idempotent_on_second_run()
    {
        await SeedAsync();

        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);

        var first = await migrator.MigrateAsync(
            new IriSqlOptions("http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);
        var firstGraphIriTouch = first.Steps
            .Single(s => s.Table == "knowledge_systems" && s.Column == "graph_iri")
            .AffectedRows;
        Assert.Equal(1, firstGraphIriTouch);

        // Second run — the row no longer contains the legacy prefix
        // so the WHERE clause matches zero rows.
        await using var db2 = BuildContext();
        var migrator2 = new IriSqlMigrator(db2, NullLogger<IriSqlMigrator>.Instance);
        var second = await migrator2.MigrateAsync(
            new IriSqlOptions("http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);
        var secondGraphIriTouch = second.Steps
            .Single(s => s.Table == "knowledge_systems" && s.Column == "graph_iri")
            .AffectedRows;
        Assert.Equal(0, secondGraphIriTouch);
    }

    [Fact]
    public async Task MigrateAsync_throws_when_from_prefix_lacks_path_separator()
    {
        await SeedAsync();
        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);

        // 'http://ontopilot.local' would otherwise substring-match
        // 'http://ontopilot.localized/' — the migrator must reject
        // unanchored prefixes up front.
        await Assert.ThrowsAsync<ArgumentException>(() => migrator.MigrateAsync(
            new IriSqlOptions(
                FromPrefix: "http://ontopilot.local",  // no trailing / or #
                ToPrefix: "http://goodcrew.local/"),
            CancellationToken.None));
    }
}
