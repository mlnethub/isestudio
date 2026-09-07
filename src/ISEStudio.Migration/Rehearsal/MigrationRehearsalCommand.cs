using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Migration.Rdf;
using ISEStudio.Migration.Sql;

namespace ISEStudio.Migration.Rehearsal;

public sealed class MigrationRehearsalCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<MigrationRehearsalManifest> RunAsync(
        MigrationRehearsalOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var runId = Guid.NewGuid().ToString("N");
        var steps = new List<MigrationStepResult>();
        var passed = true;

        async Task RunStepAsync(string name, Func<Task<(long Rows, string Checksum, string? Detail)>> action)
        {
            if (!passed)
            {
                return;
            }

            var started = DateTimeOffset.UtcNow;
            try
            {
                var result = await action().ConfigureAwait(false);
                steps.Add(new MigrationStepResult(name, "passed", result.Rows, result.Checksum,
                    result.Detail, started, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                passed = false;
                steps.Add(new MigrationStepResult(name, "failed", 0, string.Empty,
                    $"{ex.GetType().Name}: {ex.Message}", started, DateTimeOffset.UtcNow));
            }
        }

        await RunStepAsync("database-connectivity", async () =>
        {
            if (options.DatabaseMode == "restored"
                && !File.Exists(options.BackupPath))
            {
                throw new FileNotFoundException(
                    $"Verified backup artifact was not found at '{options.BackupPath}'.",
                    options.BackupPath);
            }
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return (value, "1", "PostgreSQL connection verified.");
        }).ConfigureAwait(false);

        await RunStepAsync("ef-migrations", async () =>
        {
            var dbOptions = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(options.PostgresConnectionString)
                .Options;
            await using var db = new ISEStudioDbContext(dbOptions);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
            return (applied.LongCount(), ChecksumOf(string.Join("\n", applied)),
                $"Applied migrations: {applied.Count()}.");
        }).ConfigureAwait(false);

        await RunStepAsync("schema-assertions", async () =>
        {
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            var tables = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (tables == 0) throw new InvalidOperationException("The public schema has no tables.");
            return (tables, ChecksumOf(tables.ToString()), "Public tables verified.");
        }).ConfigureAwait(false);

        await RunStepAsync("sql-snapshot", async () =>
        {
            var snapshot = await SqlSnapshot.CaptureAsync(options.PostgresConnectionString!, cancellationToken)
                .ConfigureAwait(false);
            var checksum = ChecksumOf(string.Join("\n", snapshot.BusinessChecksums.OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{snapshot.TableCounts[x.Key]}:{x.Value}")));
            var orphans = snapshot.OrphanCounts.Values.Sum();
            if (orphans != 0) throw new InvalidOperationException($"Foreign-key orphan count is {orphans}.");
            return (snapshot.TableCounts.Values.Sum(), checksum,
                $"Tables={snapshot.TableCounts.Count}; FK orphans={orphans}.");
        }).ConfigureAwait(false);

        await RunStepAsync("rdf-copy", async () =>
        {
            if (string.IsNullOrWhiteSpace(options.RdfSourcePath)
                || string.IsNullOrWhiteSpace(options.RdfCopyPath)
                || string.IsNullOrWhiteSpace(options.RdfWorkPath))
            {
                return (0L, ChecksumOf("not-configured"), "Skipped: RDF paths were not configured.");
            }

            var result = await RdfMigrationCommand.VerifyCopyAsync(
                options.RdfSourcePath, options.RdfCopyPath, options.RdfWorkPath, [], cancellationToken)
                .ConfigureAwait(false);
            if (!result.Audit.CleanupSucceeded || result.Audit.SourceOpenedByDotNet)
            {
                throw new InvalidOperationException("RDF copy safety or cleanup verification failed.");
            }
            return ((long)result.Report.QuadCount, ChecksumOf(string.Join("\n", result.Report.QueryResultHashes)),
                $"Strategy={result.Report.Strategy}; named graphs={result.Report.NamedGraphs.Count}.");
        }).ConfigureAwait(false);

        await RunStepAsync("blob-manifest", async () =>
        {
            if (string.IsNullOrWhiteSpace(options.BlobManifestPath))
            {
                return (0L, ChecksumOf("not-configured"), "Skipped: blob manifest was not configured.");
            }

            var bytes = await File.ReadAllBytesAsync(options.BlobManifestPath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Blob manifest does not contain an entries array.");
            }
            return (entries.GetArrayLength(), Convert.ToHexString(SHA256.HashData(bytes)),
                "Blob manifest JSON and checksum verified.");
        }).ConfigureAwait(false);

        await RunStepAsync("graph-read-only", async () =>
        {
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM fact) + (SELECT count(*) FROM factevidence)", connection);
            var rows = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return (rows, ChecksumOf(rows.ToString()), "Graph facts and evidence read-only counts verified.");
        }).ConfigureAwait(false);

        var manifest = new MigrationRehearsalManifest(
            runId, options.DatabaseMode, steps, passed,
            DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ManifestPath))!);
            await File.WriteAllTextAsync(options.ManifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
            if (passed)
            {
                steps.Add(new MigrationStepResult("report-serialization", "passed", 1,
                    ChecksumOf(await File.ReadAllTextAsync(options.ManifestPath, cancellationToken).ConfigureAwait(false)),
                    "Manifest written.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                manifest = manifest with { Steps = steps, CompletedAt = DateTimeOffset.UtcNow };
                await File.WriteAllTextAsync(options.ManifestPath,
                    JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            steps.Add(new MigrationStepResult("report-serialization", "failed", 0, string.Empty,
                $"{ex.GetType().Name}: {ex.Message}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            manifest = manifest with { Steps = steps, Passed = false, CompletedAt = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(options.ManifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions), CancellationToken.None).ConfigureAwait(false);
        }

        return manifest;
    }

    private static void ValidateOptions(MigrationRehearsalOptions options)
    {
        if (options.DatabaseMode is not ("fresh" or "restored" or "upgrade"))
            throw new ArgumentException("DatabaseMode must be fresh, restored, or upgrade.", nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(options.ManifestPath);
        if (options.DatabaseMode == "restored" && string.IsNullOrWhiteSpace(options.BackupPath))
            throw new ArgumentException("Restored rehearsal requires BackupPath.", nameof(options));
    }

    private static string ChecksumOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}