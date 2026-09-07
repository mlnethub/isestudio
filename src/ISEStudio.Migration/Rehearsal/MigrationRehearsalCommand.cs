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
    private static readonly JsonSerializerOptions StableJsonOptions = new()
    {
        WriteIndented = false,
    };
    private readonly IPostgresBackupValidator _backupValidator;

    public MigrationRehearsalCommand(IPostgresBackupValidator? backupValidator = null)
    {
        _backupValidator = backupValidator ?? new PgRestoreBackupValidator();
    }

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
            var modeMetadata = await ValidateDatabaseModeAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return (MigrationStepStatus.Passed, value, "1", "PostgreSQL connection and mode preconditions verified.", modeMetadata);
        }).ConfigureAwait(false);

        SnapshotResult? beforeSnapshot = null;
        await RunStepAsync("ef-migrations", async () =>
        {
            var dbOptions = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(options.PostgresConnectionString)
                .Options;
            await using var db = new ISEStudioDbContext(dbOptions);
            beforeSnapshot = await SqlSnapshot.CaptureAsync(options.PostgresConnectionString!, cancellationToken)
                .ConfigureAwait(false);
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
            var graphAfter = await CaptureGraphEvidenceAsync(options.PostgresConnectionString!, cancellationToken).ConfigureAwait(false);
            SnapshotComparison.AssertEquivalent(beforeSnapshot, afterSnapshot, graphBefore, graphAfter);
            var metadata = SnapshotMetadata(beforeSnapshot, afterSnapshot);
            metadata = metadata.Concat(new Dictionary<string, string>
            {
                ["graphBefore"] = JsonSerializer.Serialize(graphBefore, StableJsonOptions),
                ["graphAfter"] = JsonSerializer.Serialize(graphAfter, StableJsonOptions),
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

        var completedAt = DateTimeOffset.UtcNow;
        steps.Add(new MigrationStepResult("report-serialization", MigrationStepStatus.Passed, 1, string.Empty,
            "Manifest checksum covers the final JSON excluding only ManifestChecksum; report checksum covers the same payload with its own checksum cleared.",
            completedAt, completedAt));
        var manifest = new MigrationRehearsalManifest(runId, options.DatabaseMode, steps, passed, completedAt);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ManifestPath))!);
            manifest = MigrationManifestIntegrity.Finalize(manifest);
            await File.WriteAllTextAsync(options.ManifestPath, MigrationManifestIntegrity.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            steps[^1] = steps[^1] with { Status = MigrationStepStatus.Failed, Detail = $"{ex.GetType().Name}: {ex.Message}" };
            manifest = MigrationManifestIntegrity.Finalize(manifest with { Steps = steps, Passed = false });
            await File.WriteAllTextAsync(options.ManifestPath,
                MigrationManifestIntegrity.Serialize(manifest), CancellationToken.None).ConfigureAwait(false);
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

    private async Task<IReadOnlyDictionary<string, string>?> ValidateDatabaseModeAsync(NpgsqlConnection connection, MigrationRehearsalOptions options, CancellationToken cancellationToken)
    {
        if (options.DatabaseMode == "restored")
        {
            var path = options.BackupPath!;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("Restored backup must be a non-empty file.");
            var (backupSize, backupSha256) = await ComputeFileDigestAsync(path, cancellationToken).ConfigureAwait(false);
            if (options.ExpectedBackupSize is not null && options.ExpectedBackupSize.Value != backupSize)
                throw new InvalidDataException($"Backup size does not match expected value {options.ExpectedBackupSize.Value}.");
            if (!string.IsNullOrWhiteSpace(options.ExpectedBackupSha256)
                && !string.Equals(options.ExpectedBackupSha256, backupSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup SHA-256 does not match the expected value.");
            var validation = await _backupValidator.ValidateAsync(path, cancellationToken).ConfigureAwait(false);
            if (!validation.IsListable)
                throw new InvalidDataException($"Backup is not a listable pg_dump artifact: {validation.Detail}");
            var restoredTables = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'", cancellationToken);
            if (restoredTables == 0) throw new InvalidDataException("Database does not contain restored application tables.");
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["restoreVerification"] = "external/manual; database-to-artifact association remains operator evidence",
                ["backupFormat"] = validation.Format,
                ["backupSize"] = backupSize.ToString(),
                ["backupSha256"] = backupSha256,
            };
        }
        else
        {
            var history = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'", cancellationToken);
            var applicationTables = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'", cancellationToken);
            if (options.DatabaseMode == "fresh" && (history != 0 || applicationTables != 0))
                throw new InvalidOperationException("Fresh rehearsal requires an empty public schema with no EF migration history.");
            if (options.DatabaseMode == "upgrade")
            {
                var historyRows = history == 0 ? 0 : await ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\"", cancellationToken);
                var markerTable = await ScalarAsync(connection, "SELECT to_regclass('public.__migration_rehearsal_upgrade_marker') IS NOT NULL", cancellationToken);
                if (historyRows == 0 || markerTable == 0)
                    throw new InvalidOperationException("Upgrade rehearsal requires non-empty old migration history and the __migration_rehearsal_upgrade_marker table.");
                var markerColumns = await ScalarAsync(connection, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '__migration_rehearsal_upgrade_marker' AND column_name IN ('marker', 'source_migration', 'target_schema')", cancellationToken);
                if (markerColumns != 3)
                    throw new InvalidOperationException("Upgrade rehearsal marker must include marker, source_migration, and target_schema columns.");
                await using var markerCommand = new NpgsqlCommand("SELECT source_migration, target_schema FROM public.__migration_rehearsal_upgrade_marker WHERE marker = 'pre-upgrade'", connection);
                await using var markerReader = await markerCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await markerReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Upgrade rehearsal requires a pre-upgrade marker row.");
                var sourceMigration = markerReader.GetString(0);
                var targetSchema = markerReader.GetString(1);
                if (!string.Equals(targetSchema, "public", StringComparison.Ordinal))
                    throw new InvalidOperationException("Upgrade rehearsal marker target_schema must be public.");
                await markerReader.CloseAsync().ConfigureAwait(false);
                await using var historyCommand = new NpgsqlCommand("SELECT count(*) FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = @migration", connection);
                historyCommand.Parameters.AddWithValue("migration", sourceMigration);
                if (await historyCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long { } matchingHistory || matchingHistory == 0)
                    throw new InvalidOperationException("Upgrade rehearsal marker source_migration must exist in migration history.");
            }
        }
        return null;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<GraphSnapshot> CaptureGraphEvidenceAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var fact = await GraphEvidenceAsync(connection, "fact", cancellationToken).ConfigureAwait(false);
        var evidence = await GraphEvidenceAsync(connection, "factevidence", cancellationToken).ConfigureAwait(false);
        return new GraphSnapshot(fact.RowCount + evidence.RowCount, ChecksumOf($"fact:{fact.ContentChecksum}\nevidence:{evidence.ContentChecksum}"));
    }

    private static async Task<GraphSnapshot> GraphEvidenceAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, $"SELECT to_regclass('public.{table}') IS NOT NULL", cancellationToken) == 0)
        {
            return new GraphSnapshot(0, string.Empty);
        }
        await using var command = new NpgsqlCommand($"SELECT count(*)::bigint, md5(COALESCE(string_agg(row_to_json(t)::text, '' ORDER BY row_to_json(t)::text), '')) FROM public.\"{table}\" t", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new GraphSnapshot(reader.GetInt64(0), reader.GetString(1));
    }

    private static IReadOnlyDictionary<string, string> SnapshotMetadata(SnapshotResult? before, SnapshotResult after) =>
        new Dictionary<string, string>
        {
            ["before"] = before is null ? "null" : JsonSerializer.Serialize(before, StableJsonOptions),
            ["after"] = JsonSerializer.Serialize(after, StableJsonOptions),
        };

    private static string SnapshotText(SnapshotResult snapshot) =>
        string.Join("\n", snapshot.BusinessChecksums.OrderBy(x => x.Key).Select(x => $"{x.Key}:{snapshot.TableCounts[x.Key]}:{x.Value}:{string.Join(",", snapshot.OrphanCounts.OrderBy(y => y.Key).Select(y => $"{y.Key}={y.Value}"))}"));

    private static string ChecksumOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<(long Size, string Sha256)> ComputeFileDigestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            sha256.TransformBlock(buffer, 0, read, null, 0);
            size += read;
        }
        sha256.TransformFinalBlock([], 0, 0);
        return (size, Convert.ToHexString(sha256.Hash!));
    }
}