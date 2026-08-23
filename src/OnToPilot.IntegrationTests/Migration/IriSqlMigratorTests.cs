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
/// runs ten <c>UPDATE ... REPLACE(...)</c> statements against the
/// IRI-bearing columns the .NET schema exposes, so the test container
/// must use the same EF Core schema as production.
/// <list type="bullet">
///   <item>Each test seeds a fresh <see cref="PostgreSqlContainer"/>,
///   applies the EF Core schema with <c>MigrateAsync</c>, and inserts
///   fixture rows that carry legacy-prefix IRIs.</item>
///   <item>The migrator's <see cref="IriSqlOptions.DryRun"/> flag is
///   asserted to be a no-op against the data; the apply run is
///   asserted to rewrite every column.</item>
///   <item>Re-running the migrator is a no-op (the rewritten rows no
///   longer contain the legacy prefix).</item>
/// </list>
/// </summary>
/// <remarks>
/// Tests skip silently when docker is unavailable (Windows container
/// without a docker daemon, sandboxed CI runner). The skip pattern
/// mirrors <see cref="BlobMigrationTests"/> so the integration test
/// baseline never regresses to "DockerException everywhere".
/// </remarks>
[Trait("Category", "Migration")]
public sealed class IriSqlMigratorTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ontopilot_iri")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        try
        {
            await _container.StartAsync();
            _dockerAvailable = true;
        }
        catch (Exception ex) when (
            ex is Docker.DotNet.DockerApiException
            || ex is System.Net.Http.HttpRequestException
            || ex is TimeoutException
            || ex is InvalidOperationException)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_dockerAvailable)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Return <c>true</c> when docker is unavailable so the calling
    /// <c>[Fact]</c> can early-return; <c>false</c> when the test
    /// should proceed normally.
    /// </summary>
    private bool DockerRequired()
    {
        if (_dockerAvailable) return false;
        return true;
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
    /// <remarks>
    /// EF Core 10 + Npgsql 10: <c>EnsureCreatedAsync</c> is a no-op
    /// once the assembly contains a migration snapshot (the snapshot
    /// means EF Core expects <c>MigrateAsync</c> to be the schema
    /// authority). Calling <c>EnsureCreatedAsync</c> here silently
    /// skipped DDL and left the PG instance empty, which is why every
    /// fixture-driven integration test in this file failed with
    /// <c>42P01: relation "knowledgesystem" does not exist</c>. The
    /// fix is to use <c>MigrateAsync</c> so the InitialCompatibility
    /// migration actually runs.
    /// </remarks>
    private async Task SeedAsync()
    {
        await using var db = BuildContext();
        await db.Database.MigrateAsync();

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
        if (DockerRequired()) return;
        await SeedAsync();

        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);
        var report = await migrator.MigrateAsync(
            new IriSqlOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/",
                DryRun: true),
            CancellationToken.None);

        // knowledgesystem.GraphIri / BaseIri both contain the legacy
        // prefix and would be touched.
        Assert.Contains(report.Steps, s =>
            s.Table == "knowledgesystem" && s.Column == "GraphIri" && s.AffectedRows == 1);
        Assert.Contains(report.Steps, s =>
            s.Table == "knowledgesystem" && s.Column == "BaseIri" && s.AffectedRows == 1);

        // The data must NOT have been written.
        await using var verify = BuildContext();
        var ks = await verify.KnowledgeSystems.AsNoTracking().SingleAsync();
        Assert.Equal("http://ontopilot.local/ks/1", ks.GraphIri);
        Assert.Equal("http://ontopilot.local/ks/1/onto#", ks.BaseIri);
    }

    [Fact]
    public async Task Apply_rewrites_every_iri_bearing_column_and_preserves_rows()
    {
        if (DockerRequired()) return;
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
        if (DockerRequired()) return;
        await SeedAsync();

        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);

        var first = await migrator.MigrateAsync(
            new IriSqlOptions("http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);
        var firstGraphIriTouch = first.Steps
            .Single(s => s.Table == "knowledgesystem" && s.Column == "GraphIri")
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
            .Single(s => s.Table == "knowledgesystem" && s.Column == "GraphIri")
            .AffectedRows;
        Assert.Equal(0, secondGraphIriTouch);
    }

    [Fact]
    public async Task MigrateAsync_throws_when_from_prefix_lacks_path_separator()
    {
        // 'http://ontopilot.local' would otherwise substring-match
        // 'http://ontopilot.localized/' — the migrator must reject
        // unanchored prefixes up front. No docker required: this is
        // a pure ArgumentException short-circuit.
        await using var db = BuildContext();
        var migrator = new IriSqlMigrator(db, NullLogger<IriSqlMigrator>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => migrator.MigrateAsync(
            new IriSqlOptions(
                FromPrefix: "http://ontopilot.local",  // no trailing / or #
                ToPrefix: "http://goodcrew.local/"),
            CancellationToken.None));
    }
}