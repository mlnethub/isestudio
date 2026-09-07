using System.Text.Json;
using ISEStudio.Migration.Rehearsal;

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
                "database-connectivity", "passed", 0, "ABC", "connected",
                DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-09-07T00:00:01+00:00"))],
            true,
            DateTimeOffset.Parse("2026-09-07T00:00:00+00:00"));

        var json = JsonSerializer.Serialize(manifest);
        var restored = JsonSerializer.Deserialize<MigrationRehearsalManifest>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.Passed);
        Assert.Equal("database-connectivity", Assert.Single(restored.Steps).Name);
        Assert.Equal("passed", restored.Steps[0].Status);
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
            Assert.Contains(result.Steps, step => step.Status == "failed");
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }
}