using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Migration.Rehearsal;
using ISEStudio.Migration.Sql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Migration;

[Trait("Category", "Migration")]
public sealed class MigrationRehearsalTests
{
    [Fact]
    public void Snapshot_comparison_rejects_deleted_or_changed_business_data_and_graph_evidence()
    {
        var before = new SnapshotResult(
            new Dictionary<string, long> { ["document"] = 2 },
            new Dictionary<string, long> { ["document.userid"] = 0 },
            new Dictionary<string, string> { ["document"] = "document-checksum" });
        var after = before with
        {
            TableCounts = new Dictionary<string, long> { ["document"] = 1 },
            BusinessChecksums = new Dictionary<string, string> { ["document"] = "tampered-checksum" },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SnapshotComparison.AssertEquivalent(before, after, new GraphSnapshot(1, "before"), new GraphSnapshot(0, "after")));

        Assert.Contains("document", exception.Message);
        Assert.Contains("graph", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_comparison_accepts_empty_and_unchanged_fixture_evidence()
    {
        var empty = new SnapshotResult(
            new Dictionary<string, long> { ["document"] = 0 },
            new Dictionary<string, long>(),
            new Dictionary<string, string> { ["document"] = string.Empty });

        var result = SnapshotComparison.Compare(empty, empty, new GraphSnapshot(0, string.Empty), new GraphSnapshot(0, string.Empty));

        Assert.True(result.IsEquivalent);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Manifest_integrity_rejects_tampered_passed_steps_and_metadata()
    {
        var manifest = new MigrationRehearsalManifest(
            "run-1", "fresh", [new MigrationStepResult(
                "report-serialization", MigrationStepStatus.Passed, 1, string.Empty, "fixture",
                DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"))], true,
            DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"));
        var serialized = MigrationManifestIntegrity.Serialize(MigrationManifestIntegrity.Finalize(manifest));
        var tampered = serialized.Replace("\"Passed\":true", "\"Passed\":false", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => MigrationManifestIntegrity.Validate(tampered));
    }

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
    public async Task Restored_rehearsal_rejects_non_dump_even_when_target_has_tables()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"backup-{Guid.NewGuid():N}.dump");
        try
        {
            await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE TABLE restored_fixture (id integer primary key)", connection);
                await command.ExecuteNonQueryAsync();
            }
            await File.WriteAllTextAsync(backupPath, "this is not a PostgreSQL dump");

            var result = await new MigrationRehearsalCommand(new StubBackupValidator(false))
                .RunAsync(new MigrationRehearsalOptions("restored", _container.GetConnectionString(), manifestPath, backupPath), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("listable pg_dump artifact", result.Steps.Single(step => step.Name == "database-connectivity").Detail);
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task Restored_rehearsal_rejects_empty_target_even_when_backup_is_listable()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"backup-{Guid.NewGuid():N}.dump");
        try
        {
            await File.WriteAllBytesAsync(backupPath, [1, 2, 3]);
            var result = await new MigrationRehearsalCommand(new StubBackupValidator(true))
                .RunAsync(new MigrationRehearsalOptions("restored", _container.GetConnectionString(), manifestPath, backupPath), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("application tables", result.Steps.Single(step => step.Name == "database-connectivity").Detail);
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task Upgrade_rehearsal_requires_explicit_pre_upgrade_marker()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var result = await new MigrationRehearsalCommand()
                .RunAsync(new MigrationRehearsalOptions("upgrade", _container.GetConnectionString(), manifestPath), CancellationToken.None);

            Assert.False(result.Passed);
            var detail = result.Steps.Single(step => step.Name == "database-connectivity").Detail!;
            Assert.True(detail.Contains("old migration history", StringComparison.Ordinal)
                || detail.Contains("pre-upgrade marker", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public async Task Upgrade_rehearsal_runs_against_old_schema_fixture_with_pending_migrations()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"rehearsal-{Guid.NewGuid():N}.json");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(_container.GetConnectionString())
                .Options;
            await using (var db = new ISEStudioDbContext(dbOptions))
            {
                await db.Database.MigrateAsync("20260904151546_AddKnowledgeGraph");
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE __migration_rehearsal_upgrade_marker (marker text primary key)");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO __migration_rehearsal_upgrade_marker (marker) VALUES ('pre-upgrade')");
            }

            var result = await new MigrationRehearsalCommand()
                .RunAsync(new MigrationRehearsalOptions("upgrade", _container.GetConnectionString(), manifestPath), CancellationToken.None);

            Assert.Equal(MigrationStepStatus.Passed, result.Steps.Single(step => step.Name == "ef-migrations").Status);
            Assert.Contains("before", result.Steps.Single(step => step.Name == "sql-snapshot").Metadata!.Keys);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

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

    private sealed class StubBackupValidator(bool isListable) : IPostgresBackupValidator
    {
        public Task<BackupValidationResult> ValidateAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new BackupValidationResult(isListable, "fixture", isListable ? "fixture accepted" : "fixture rejected"));
    }
}