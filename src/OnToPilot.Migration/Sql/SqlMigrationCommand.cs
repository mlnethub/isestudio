using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace OnToPilot.Migration.Sql;

/// <summary>
/// Applies (and reverses) the SQL migration that bridges the Python /
/// SQLAlchemy schema into the .NET / EF Core schema for the 24 business
/// tables. Every step is idempotent: re-running <see cref="ApplyAsync"/>
/// is a no-op once the database is in the target state.
///
/// <para>The migration is the first of three cutover layers (this one is
/// SQL, then RDF, then blobs). The brief is explicit: any failure here
/// must stop the cutover; we do not auto-proceed to RDF or blobs.</para>
/// </summary>
/// <remarks>
/// <para>The SQL files live under
/// <c>migrations/SqlAlchemyToEfCore/</c> in the repository root and are
/// copied next to the assembly at build time. They are loaded by
/// resource name at runtime; the working directory at execution time
/// does not have to be the repository root.</para>
/// </remarks>
public sealed class SqlMigrationCommand
{
    private readonly ILogger<SqlMigrationCommand> _logger;
    private readonly string _migrationsDirectory;

    /// <summary>The SQL files applied in order by <see cref="ApplyAsync"/>.</summary>
    private static readonly string[] ApplySteps =
    {
        "001_add_guid_and_legacy_ids.sql",
        "002_backfill_foreign_keys.sql",
        "003_apply_ef_constraints.sql",
    };

    /// <summary>
    /// Create a command bound to the migrations directory shipped with the
    /// assembly. By default the loader scans both the assembly directory
    /// and the repo-root <c>migrations/SqlAlchemyToEfCore</c> so the
    /// command works from test runs, CLI invocation, and the rehearsal
    /// orchestration (Task 4) without configuration.
    /// </summary>
    public SqlMigrationCommand(ILogger<SqlMigrationCommand> logger)
        : this(logger, ResolveMigrationsDirectory())
    {
    }

    /// <summary>Test / orchestration seam that injects the migrations directory explicitly.</summary>
    public SqlMigrationCommand(ILogger<SqlMigrationCommand> logger, string migrationsDirectory)
    {
        _logger = logger;
        _migrationsDirectory = migrationsDirectory;
    }

    /// <summary>
    /// Apply the migration to <paramref name="connectionString"/>. Returns
    /// a <see cref="MigrationLog"/> describing the steps actually executed
    /// (the log is also written to disk under the migrations directory so
    /// the rollback path and Task 4's <c>Assert-AllMigrationManifests</c>
    /// gate can find it).
    /// </summary>
    public async Task<MigrationLog> ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        var log = new MigrationLog(StartedAt: DateTimeOffset.UtcNow);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        foreach (var step in ApplySteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sql = await LoadScriptAsync(step, cancellationToken);
            _logger.LogInformation("Applying {Step} ({Length} chars)", step, sql.Length);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            log.Steps.Add(new MigrationStep(step, DateTimeOffset.UtcNow, ChecksumOf(sql)));
        }

        // Run verify.sql and embed its summary in the log.
        var verify = await LoadScriptAsync("verify.sql", cancellationToken);
        var summary = await RunVerifyAsync(conn, verify, cancellationToken);
        log.VerifySummary = summary;

        log.FinishedAt = DateTimeOffset.UtcNow;
        await log.WriteAsync(Path.Combine(_migrationsDirectory, "migration-log.json"), cancellationToken);
        _logger.LogInformation("Migration applied; log written to {Path}", Path.Combine(_migrationsDirectory, "migration-log.json"));
        return log;
    }

    /// <summary>
    /// Reverse the migration. The original bigint <c>id</c> primary key is
    /// never dropped by <see cref="ApplyAsync"/>, so rollback only needs to
    /// drop the new columns, indexes, and FK constraints that were added
    /// on top.
    /// </summary>
    public async Task<MigrationLog> RollbackAsync(string connectionString, CancellationToken cancellationToken)
    {
        var log = new MigrationLog(StartedAt: DateTimeOffset.UtcNow);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = await LoadScriptAsync("rollback.sql", cancellationToken);
        _logger.LogInformation("Rolling back migration ({Length} chars)", sql.Length);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        log.Steps.Add(new MigrationStep("rollback.sql", DateTimeOffset.UtcNow, ChecksumOf(sql)));

        log.FinishedAt = DateTimeOffset.UtcNow;
        await log.WriteAsync(Path.Combine(_migrationsDirectory, "migration-log.json"), cancellationToken);
        _logger.LogInformation("Migration rolled back; log written to {Path}", Path.Combine(_migrationsDirectory, "migration-log.json"));
        return log;
    }

    /// <summary>
    /// Run the supplied verify.sql as a single query batch. The script is
    /// expected to <c>SELECT</c> rows of <c>(table_name, row_count,
    /// orphan_count, business_checksum)</c>; we return them as a structured
    /// summary.
    /// </summary>
    private async Task<MigrationVerifySummary> RunVerifyAsync(NpgsqlConnection conn, string verifySql, CancellationToken cancellationToken)
    {
        var summary = new MigrationVerifySummary();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = verifySql;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summary.Rows.Add(new MigrationVerifyRow(
                Table: reader.GetString(0),
                RowCount: reader.GetInt64(1),
                OrphanCount: reader.GetInt64(2),
                BusinessChecksum: reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }
        return summary;
    }

    private async Task<string> LoadScriptAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_migrationsDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Migration script '{fileName}' not found in '{_migrationsDirectory}'.",
                path);
        }
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private static string ChecksumOf(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string ResolveMigrationsDirectory()
    {
        // The MSBuild target copies the SQL files next to the executing
        // assembly. That is the authoritative location at runtime; the
        // repository root fallback is only used by a developer running
        // `dotnet run` from the OnToPilot.Migration project without the
        // target having copied anything yet.
        var asmDir = Path.GetDirectoryName(typeof(SqlMigrationCommand).Assembly.Location)!;
        var alongside = Path.Combine(asmDir, "migrations", "SqlAlchemyToEfCore");
        if (Directory.Exists(alongside))
        {
            return alongside;
        }

        // Walk up the directory tree until we find a sibling migrations
        // directory; this lets the test harness and the rehearsal
        // orchestrator run from the repo root.
        var current = new DirectoryInfo(asmDir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "migrations", "SqlAlchemyToEfCore");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate migrations/SqlAlchemyToEfCore next to the assembly or anywhere up the directory tree.");
    }
}

/// <summary>One applied step in a migration log.</summary>
public sealed record MigrationStep(string FileName, DateTimeOffset AppliedAt, string Checksum);

/// <summary>One row of the verify.sql summary.</summary>
public sealed record MigrationVerifyRow(string Table, long RowCount, long OrphanCount, string BusinessChecksum);

/// <summary>
/// Output of <c>verify.sql</c>: one row per business table with the
/// post-migration row count, the foreign-key orphan count, and a
/// deterministic business checksum.
/// </summary>
public sealed class MigrationVerifySummary
{
    public List<MigrationVerifyRow> Rows { get; } = new();
}

/// <summary>
/// The on-disk record of a migration run. Persisted to
/// <c>migrations/SqlAlchemyToEfCore/migration-log.json</c> so the rollback
/// path and Task 4's <c>Assert-AllMigrationManifests</c> gate can find it.
/// </summary>
public sealed class MigrationLog
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<MigrationStep> Steps { get; } = new();
    public MigrationVerifySummary? VerifySummary { get; set; }

    public MigrationLog(DateTimeOffset StartedAt)
    {
        this.StartedAt = StartedAt;
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
    }
}
