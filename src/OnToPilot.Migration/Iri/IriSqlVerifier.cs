using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Migration.Iri;

/// <summary>
/// Input for <see cref="IriSqlVerifier"/> — the IRI prefix pair whose
/// <c>FromPrefix</c> must be ABSENT from every IRI-bearing column
/// after the migrator runs. <c>ToPrefix</c> is recorded on the report
/// for operator audit but not asserted by the verifier.
/// </summary>
/// <param name="FromPrefix">Legacy prefix that must NOT appear anywhere
/// in any IRI-bearing column, e.g. <c>"http://ontopilot.local/"</c>.
/// Must end with <c>/</c> or <c>#</c> so accidental substring matches
/// cannot happen.</param>
/// <param name="ToPrefix">Target prefix recorded on the report for
/// operator audit, e.g. <c>"http://goodcrew.local/"</c>.</param>
public sealed record IriSqlVerifyOptions(string FromPrefix, string ToPrefix);

/// <summary>
/// One column-verification step. Reports both the residual count
/// (rows still containing the legacy prefix) and the table's total
/// row count so the operator can distinguish a populated column with
/// zero residual (clean migration) from a genuinely empty column
/// (vacuous pass).
/// </summary>
/// <param name="Table">EF Core entity table name.</param>
/// <param name="Column">Column scanned for residual legacy-prefix
/// rows.</param>
/// <param name="ResidualOldPrefixRows"><c>COUNT(*) WHERE col LIKE
/// from || '%'</c>. Zero = the migrator removed the legacy prefix
/// from every row in this column. Non-zero = the migrator missed
/// something; the cutover gate must stop.</param>
/// <param name="TableTotalRows"><c>COUNT(*)</c> of the whole table.
/// Surfaced for transparency; not asserted.</param>
public sealed record IriSqlVerifyStep(
    string Table,
    string Column,
    long ResidualOldPrefixRows,
    long TableTotalRows);

/// <summary>
/// Composite result of <see cref="IriSqlVerifier.VerifyAsync"/>.
/// <see cref="Steps"/> lists every column the verifier scanned;
/// <see cref="ResidualTotal"/> is the aggregate residual across the
/// lot; <see cref="FailingSteps"/> is a convenience projection for
/// the gate to enumerate residual-bearing columns without
/// re-filtering.
/// </summary>
public sealed class IriSqlVerifyReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string FromPrefix { get; init; } = string.Empty;
    public string ToPrefix { get; init; } = string.Empty;
    public List<IriSqlVerifyStep> Steps { get; set; } = new();

    /// <summary>Aggregate residual across every column; zero = pass.</summary>
    public long ResidualTotal => Steps.Sum(s => s.ResidualOldPrefixRows);

    /// <summary>Steps whose residual &gt; 0. Convenience for the gate.</summary>
    public IEnumerable<IriSqlVerifyStep> FailingSteps =>
        Steps.Where(s => s.ResidualOldPrefixRows > 0);

    /// <summary>
    /// Serialise the report to disk for the cutover record audit
    /// trail. Mirrors <see cref="IriSqlReport.WriteAsync"/>: indented
    /// JSON via <see cref="System.Text.Json.JsonSerializer"/>.
    /// </summary>
    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Compute the canonical SHA-256 of the report so the cutover
    /// record can pin the audit trail. Reads back the bytes that
    /// <see cref="WriteAsync"/> just wrote so the digest is exactly
    /// what the cutover record will see.
    /// </summary>
    public static string ComputeReportSha256(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Post-migration smoke-check. Proves <see cref="IriSqlMigrator"/>
/// actually rewrote the legacy-prefix rows in every IRI-bearing
/// column, instead of trusting the migrator's <c>AffectedRows</c>
/// report (which is zero on idempotent re-runs and therefore
/// vacuously true).
/// </summary>
public sealed class IriSqlVerifier
{
    private readonly OnToPilotDbContext _db;
    private readonly ILogger<IriSqlVerifier> _logger;

    public IriSqlVerifier(OnToPilotDbContext db, ILogger<IriSqlVerifier> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Run the verification. For each (table, column) tuple exposed
    /// by <see cref="IriSqlMigrator.ColumnsToRewritePublic"/>, runs
    /// <c>SELECT COUNT(*) WHERE col LIKE from || '%'</c> and
    /// <c>SELECT COUNT(*)</c> on the table. Returns a populated
    /// <see cref="IriSqlVerifyReport"/>; throws
    /// <see cref="IriSqlVerificationException"/> if any column still
    /// contains the legacy prefix.
    /// </summary>
    public async Task<IriSqlVerifyReport> VerifyAsync(
        IriSqlVerifyOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateFromPrefix(options.FromPrefix);

        var report = new IriSqlVerifyReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            FromPrefix = options.FromPrefix,
            ToPrefix = options.ToPrefix,
        };

        var likePattern = options.FromPrefix + "%";
        var failures = new List<string>();

        foreach (var (table, column) in IriSqlMigrator.ColumnsToRewritePublic)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // EF1002: SqlQueryRaw<long> 故意把 table/column 作为字面量
            // 拼接(来自静态 ColumnsToRewrite 元组);前缀走 positional
            // {0} 参数。与 IriSqlMigrator 同根处理。
#pragma warning disable EF1002
            var residualRows = await _db.Database
                .SqlQueryRaw<long>(
                    $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\" WHERE \"{column}\" LIKE {{0}}",
                    likePattern)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var totalRows = await _db.Database
                .SqlQueryRaw<long>(
                    $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\"",
                    Array.Empty<object>())
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore EF1002

            var residual = residualRows.Count > 0 ? residualRows[0] : 0L;
            var total = totalRows.Count > 0 ? totalRows[0] : 0L;

            report.Steps.Add(new IriSqlVerifyStep(table, column, residual, total));

            if (residual > 0)
            {
                failures.Add(
                    $"{table}.{column}: {residual} row(s) still contain "
                    + $"'{options.FromPrefix}' (table total rows = {total}).");
            }

            _logger.LogInformation(
                "[verify] {Table}.{Column}: residual={Residual} total={Total}",
                table, column, residual, total);
        }

        report.FinishedAt = DateTimeOffset.UtcNow;

        if (failures.Count > 0)
        {
            throw new IriSqlVerificationException(failures);
        }

        return report;
    }

    private static void ValidateFromPrefix(string fromPrefix)
    {
        if (string.IsNullOrEmpty(fromPrefix))
        {
            throw new ArgumentException("FromPrefix must be non-empty.", nameof(fromPrefix));
        }
        // Anchor on a path / fragment separator so the LIKE cannot
        // hit a stray substring (e.g. an unrelated string column that
        // happens to contain the from prefix as a suffix). Mirrors
        // IriSqlMigrator.ValidatePrefixes.
        if (!(fromPrefix.EndsWith('/') || fromPrefix.EndsWith('#')))
        {
            throw new ArgumentException(
                $"FromPrefix must end with '/' or '#' (got '{fromPrefix}'). "
                + "This guards against substring collisions like 'http://ontopilot.localized/...'.",
                nameof(fromPrefix));
        }
    }
}

/// <summary>
/// Aggregated residual-report failure. The message lists every
/// column whose residual &gt; 0 so the operator sees the full
/// picture on a single throw (mirrors the failure-aggregation
/// philosophy of <c>Assert-AllMigrationManifests</c> in
/// <c>migration/scripts/gates/CutoverGates.ps1</c>).
/// </summary>
public sealed class IriSqlVerificationException : Exception
{
    public IReadOnlyList<string> Failures { get; }

    public IriSqlVerificationException(IReadOnlyList<string> failures)
        : base("One or more IRI SQL columns still contain the legacy prefix: "
               + string.Join(" | ", failures))
    {
        Failures = failures;
    }
}
