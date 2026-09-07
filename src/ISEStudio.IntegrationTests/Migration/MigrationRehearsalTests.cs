using System.Text.Json;
using ISEStudio.Migration.Rehearsal;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Migration;

[Trait("Category", "Migration")]
public sealed class MigrationRehearsalTests
{
    [Fact]
    public void Manifest_round_trips_named_steps_and_terminal_status()
    {
        var manifest = new MigrationRehearsalManifest(
            "run-1",
            "fresh",
            [new MigrationStepResult(
                "database-connectivity", MigrationStepStatus.Passed, 0, "ABC", "connected",
                DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-09-07T00:00:01+00:00"))],
            true,
            DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"));

        var json = JsonSerializer.Serialize(manifest);
        var restored = JsonSerializer.Deserialize<MigrationRehearsalManifest>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.Passed);
        Assert.Equal("database-connectivity", Assert.Single(restored.Steps).Name);
        Assert.Equal(MigrationStepStatus.Passed, restored.Steps[0].Status);
    }

    [Fact]
    public async Task Missing_database_configuration_writes_a_failed_manifest()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var command = new MigrationRehearsalCommand();
            var result = await command.RunAsync(
                new MigrationRehearsalOptions("fresh", null, manifestPath),
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(result.Steps, step => step.Status == MigrationStepStatus.Failed);
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public async Task Missing_database_configuration_stops_before_optional_steps()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var command = new MigrationRehearsalCommand();
            var result = await command.RunAsync(
                new MigrationRehearsalOptions("fresh", null, manifestPath),
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.DoesNotContain(result.Steps, step => step.Name == "rdf-copy");
            Assert.DoesNotContain(result.Steps, step => step.Name == "blob-manifest");
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Theory]
    [InlineData("restored", null)]
    [InlineData("upgrade", null)]
    public async Task Restored_and_upgrade_require_state_preconditions(string mode, string? backup)
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var command = new MigrationRehearsalCommand();
            if (mode == "restored")
            {
                await Assert.ThrowsAsync<ArgumentException>(() => command.RunAsync(
                    new MigrationRehearsalOptions(mode, null, manifestPath, backup),
                    CancellationToken.None));
            }
            else if (mode == "upgrade")
            {
                await Assert.ThrowsAsync<ArgumentException>(() => command.RunAsync(
                    new MigrationRehearsalOptions(mode, null, manifestPath, backup),
                    CancellationToken.None));
            }
            else
            {
                var result = await command.RunAsync(
                    new MigrationRehearsalOptions(mode, null, manifestPath, backup),
                    CancellationToken.None);
                Assert.False(result.Passed);
                Assert.Contains(result.Steps, step => step.Status == MigrationStepStatus.Failed);
            }
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public void Manifest_exposes_before_and_after_snapshot_evidence()
    {
        var manifest = new MigrationRehearsalManifest(
            "run-1", "fresh", [], false, DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(manifest);

        Assert.Contains("Steps", json);
        Assert.Contains("Passed", json);
    }
}

[Trait("Category", "Migration")]
public sealed class PostgreSqlMigrationRehearsalTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Fresh_rehearsal_runs_real_migrate_and_records_before_after_evidence()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var result = await new MigrationRehearsalCommand().RunAsync(
                new MigrationRehearsalOptions("fresh", _container.GetConnectionString(), manifestPath),
                CancellationToken.None);

            Assert.False(result.Passed, "Optional RDF/blob evidence must prevent acceptance.");
            Assert.Equal(MigrationStepStatus.Skipped, Assert.Single(result.Steps, step => step.Name == "rdf-copy").Status);
            Assert.Equal(MigrationStepStatus.Skipped, Assert.Single(result.Steps, step => step.Name == "blob-manifest").Status);
            Assert.Equal(MigrationStepStatus.Passed, Assert.Single(result.Steps, step => step.Name == "ef-migrations").Status);
            Assert.Equal(MigrationStepStatus.Passed, Assert.Single(result.Steps, step => step.Name == "sql-snapshot").Status);
            Assert.NotNull(result.ManifestChecksum);

            var snapshotStep = Assert.Single(result.Steps, step => step.Name == "sql-snapshot");
            Assert.NotNull(snapshotStep.Metadata);
            using var before = JsonDocument.Parse(snapshotStep.Metadata!["before"]);
            using var after = JsonDocument.Parse(snapshotStep.Metadata["after"]);
            Assert.NotEqual(JsonValueKind.Null, before.RootElement.ValueKind);
            Assert.Equal(JsonValueKind.Object, after.RootElement.ValueKind);
            var migrationStep = Assert.Single(result.Steps, step => step.Name == "ef-migrations");
            Assert.NotNull(migrationStep.Metadata);
            using var graphBefore = JsonDocument.Parse(migrationStep.Metadata!["graphBefore"]);
            using var graphAfter = JsonDocument.Parse(migrationStep.Metadata["graphAfter"]);
            Assert.Equal(JsonValueKind.Object, graphBefore.RootElement.ValueKind);
            Assert.Equal(JsonValueKind.Object, graphAfter.RootElement.ValueKind);
            Assert.Equal(MigrationStepStatus.Passed, Assert.Single(result.Steps, step => step.Name == "report-serialization").Status);
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }
}