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
    private static readonly JsonSerializerOptions StableJsonOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    public async Task<MigrationRehearsalManifest> RunAsync(
        MigrationRehearsalOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var runId = Guid.NewGuid().ToString("N");
        var steps = new List<MigrationStepResult>();
        var passed = true;
        var terminalFailure = false;

        async Task RunStepAsync(
            string name,
            Func<Task<(MigrationStepStatus Status, long Rows, string Checksum, string? Detail, IReadOnlyDictionary<string, string>? Metadata)>> action)
        {
            if (terminalFailure)
            {
                return;
            }

            var started = DateTimeOffset.UtcNow;
            try
            {
                var result = await action().ConfigureAwait(false);
                steps.Add(new MigrationStepResult(name, result.Status, result.Rows, result.Checksum,
                    result.Detail, started, DateTimeOffset.UtcNow, result.Metadata));
                if (result.Status is MigrationStepStatus.Failed or MigrationStepStatus.Skipped)
                {
                    passed = false;
                }
                if (result.Status == MigrationStepStatus.Failed)
                {
                    terminalFailure = true;
                }
            }
            catch (Exception ex)
            {
                passed = false;
                terminalFailure = true;
                steps.Add(new MigrationStepResult(name, MigrationStepStatus.Failed, 0, string.Empty,
                    $"{ex.GetType().Name}: {ex.Message}", started, DateTimeOffset.UtcNow));
            }
        }

        await RunStepAsync("database-connectivity", async () =>
        {
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            await ValidateDatabaseModeAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return (MigrationStepStatus.Passed, value, "1", "PostgreSQL connection and mode preconditions verified.", null);
        }).ConfigureAwait(false);

        SnapshotResult? beforeSnapshot = null;
        await RunStepAsync("ef-migrations", async () =>
        {
            var dbOptions = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(options.PostgresConnectionString)
                .Options;
            await using var db = new ISEStudioDbContext(dbOptions);
            if (options.DatabaseMode is "fresh" or "upgrade")
            {
                beforeSnapshot = await SqlSnapshot.CaptureAsync(options.PostgresConnectionString!, cancellationToken)
                    .ConfigureAwait(false);
            }
            var graphBefore = await CaptureGraphEvidenceAsync(options.PostgresConnectionString!, cancellationToken).ConfigureAwait(false);
            var appliedBefore = await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
            var pendingBefore = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (options.DatabaseMode == "upgrade" && (appliedBefore.Count() == 0 || !pendingBefore.Any()))
            {
                throw new InvalidOperationException("Upgrade rehearsal requires non-empty old migration history and pending migrations.");
            }
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
            var afterSnapshot = await SqlSnapshot.CaptureAsync(options.PostgresConnectionString!, cancellationToken)
                .ConfigureAwait(false);
            var metadata = SnapshotMetadata(beforeSnapshot, afterSnapshot);
            var graphAfter = await CaptureGraphEvidenceAsync(options.PostgresConnectionString!, cancellationToken).ConfigureAwait(false);
            metadata = metadata.Concat(new Dictionary<string, string>
            {
                ["graphBefore"] = graphBefore,
                ["graphAfter"] = graphAfter,
            }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return (MigrationStepStatus.Passed, applied.LongCount(), ChecksumOf(string.Join("\n", applied)),
                $"Applied migrations: {applied.Count()}; before/after snapshot evidence captured.", metadata);
        }).ConfigureAwait(false);

        await RunStepAsync("schema-assertions", async () =>
        {
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
            var tables = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (tables == 0) throw new InvalidOperationException("The public schema has no tables.");
            return (MigrationStepStatus.Passed, tables, ChecksumOf(tables.ToString()), "Public tables verified.", null);
        }).ConfigureAwait(false);

        await RunStepAsync("sql-snapshot", async () =>
        {
            var snapshot = await SqlSnapshot.CaptureAsync(options.PostgresConnectionString!, cancellationToken).ConfigureAwait(false);
            var checksum = ChecksumOf(SnapshotText(snapshot));
            var orphans = snapshot.OrphanCounts.Values.Sum();
            if (orphans != 0) throw new InvalidOperationException($"Foreign-key orphan count is {orphans}.");
            return (MigrationStepStatus.Passed, snapshot.TableCounts.Values.Sum(), checksum,
                $"Tables={snapshot.TableCounts.Count}; FK orphans={orphans}.",
                SnapshotMetadata(beforeSnapshot, snapshot));
        }).ConfigureAwait(false);

        await RunStepAsync("rdf-copy", async () =>
        {
            if (string.IsNullOrWhiteSpace(options.RdfSourcePath)
                || string.IsNullOrWhiteSpace(options.RdfCopyPath)
                || string.IsNullOrWhiteSpace(options.RdfWorkPath))
            {
                return (MigrationStepStatus.Skipped, 0L, ChecksumOf("not-configured"), "Skipped: RDF paths were not configured.", null);
            }

            var result = await RdfMigrationCommand.VerifyCopyAsync(
                options.RdfSourcePath, options.RdfCopyPath, options.RdfWorkPath, [], cancellationToken)
                .ConfigureAwait(false);
            if (!result.Audit.CleanupSucceeded || result.Audit.SourceOpenedByDotNet)
            {
                throw new InvalidOperationException("RDF copy safety or cleanup verification failed.");
            }
            return (MigrationStepStatus.Passed, (long)result.Report.QuadCount, ChecksumOf(string.Join("\n", result.Report.QueryResultHashes)),
                $"Strategy={result.Report.Strategy}; named graphs={result.Report.NamedGraphs.Count}.", null);
        }).ConfigureAwait(false);

        await RunStepAsync("blob-manifest", async () =>
        {
            if (string.IsNullOrWhiteSpace(options.BlobManifestPath))
            {
                return (MigrationStepStatus.Skipped, 0L, ChecksumOf("not-configured"), "Skipped: blob manifest was not configured.", null);
            }

            if (!File.Exists(options.BlobManifestPath) || new FileInfo(options.BlobManifestPath).Length == 0)
                throw new InvalidDataException("Blob manifest must be a non-empty file.");
            var bytes = await File.ReadAllBytesAsync(options.BlobManifestPath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Blob manifest does not contain an entries array.");
            }
            return (MigrationStepStatus.Passed, entries.GetArrayLength(), Convert.ToHexString(SHA256.HashData(bytes)),
                "Blob manifest JSON and checksum verified.", null);
        }).ConfigureAwait(false);

        await RunStepAsync("graph-read-only", async () =>
        {
            await using var connection = new NpgsqlConnection(options.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM fact) + (SELECT count(*) FROM factevidence)", connection);
            var rows = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            return (MigrationStepStatus.Passed, rows, ChecksumOf(rows.ToString()), "Graph facts and evidence read-only counts verified.", null);
        }).ConfigureAwait(false);

        steps.Add(new MigrationStepResult("report-serialization", MigrationStepStatus.Started, 0, string.Empty,
            "Final manifest checksum is calculated over the canonical manifest with checksum fields cleared.",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        steps[^1] = steps[^1] with { Status = MigrationStepStatus.Passed, Rows = 1, Checksum = string.Empty };
        var manifest = new MigrationRehearsalManifest(runId, options.DatabaseMode, steps, passed, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ManifestPath))!);
            var checksum = ComputeManifestChecksum(manifest);
            steps[^1] = steps[^1] with { Checksum = checksum, Detail = "Manifest written." };
            manifest = manifest with { Steps = steps, ManifestChecksum = checksum, CompletedAt = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = MigrationStepStatus.Failed, Detail = $"{ex.GetType().Name}: {ex.Message}" };
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
        if (options.DatabaseMode == "upgrade" && string.IsNullOrWhiteSpace(options.PostgresConnectionString))
            throw new ArgumentException("Upgrade rehearsal requires a PostgreSQL connection string.", nameof(options));
        foreach (var path in new[] { options.RdfSourcePath, options.RdfCopyPath, options.RdfWorkPath })
        {
            if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
                throw new DirectoryNotFoundException($"RDF path must be an existing directory: {path}");
        }
    }

    private static async Task ValidateDatabaseModeAsync(NpgsqlConnection connection, MigrationRehearsalOptions options, CancellationToken cancellationToken)
    {
        if (options.DatabaseMode == "restored")
        {
            var path = options.BackupPath!;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("Restored backup must be a non-empty file.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var isCustomDump = bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "PGDMP";
            var text = Encoding.UTF8.GetString(bytes);
            if (!isCustomDump && !text.Contains("PostgreSQL database dump", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup is not a recognizable PostgreSQL dump artifact.");
            var restoredTables = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'", cancellationToken);
            if (restoredTables == 0) throw new InvalidDataException("Database does not contain restored application tables.");
        }
        else
        {
            var history = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'", cancellationToken);
            var applicationTables = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'", cancellationToken);
            if (options.DatabaseMode == "fresh" && (history != 0 || applicationTables != 0))
                throw new InvalidOperationException("Fresh rehearsal requires an empty public schema with no EF migration history.");
            if (options.DatabaseMode == "upgrade" && history == 0)
                throw new InvalidOperationException("Upgrade rehearsal requires existing EF migration history.");
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> CaptureGraphEvidenceAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var fact = await GraphCountAsync(connection, "fact", cancellationToken).ConfigureAwait(false);
        var evidence = await GraphCountAsync(connection, "factevidence", cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { fact, evidence }, StableJsonOptions);
    }

    private static async Task<long> GraphCountAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, $"SELECT to_regclass('public.{table}') IS NOT NULL", cancellationToken) == 0)
        {
            return 0;
        }
        return await ScalarAsync(connection, $"SELECT count(*) FROM public.\"{table}\"", cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> SnapshotMetadata(SnapshotResult? before, SnapshotResult after) =>
        new Dictionary<string, string>
        {
            ["before"] = before is null ? "null" : JsonSerializer.Serialize(before, StableJsonOptions),
            ["after"] = JsonSerializer.Serialize(after, StableJsonOptions),
        };

    private static string SnapshotText(SnapshotResult snapshot) =>
        string.Join("\n", snapshot.BusinessChecksums.OrderBy(x => x.Key).Select(x => $"{x.Key}:{snapshot.TableCounts[x.Key]}:{x.Value}:{snapshot.OrphanCounts.Values.Sum()}"));

    private static string ComputeManifestChecksum(MigrationRehearsalManifest manifest)
    {
        var canonicalSteps = manifest.Steps.Select(step => step with { Checksum = step.Name == "report-serialization" ? string.Empty : step.Checksum }).ToArray();
        var canonical = manifest with { Steps = canonicalSteps, ManifestChecksum = null };
        return ChecksumOf(JsonSerializer.Serialize(canonical, StableJsonOptions));
    }

    private static string ChecksumOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}