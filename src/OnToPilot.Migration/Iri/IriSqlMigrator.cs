using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Migration.Iri;

/// <summary>
/// Input for <see cref="IriSqlMigrator"/> — the IRI prefix pair plus the
/// dry-run flag. <c>DryRun</c> computes the per-table would-be row counts
/// without mutating the database, so the cutover gate can preview the
/// blast radius before flipping the switch.
/// </summary>
/// <param name="FromPrefix">Legacy prefix to replace, e.g.
/// <c>"http://ontopilot.local/"</c>. Must end with <c>/</c> or
/// <c>#</c> so accidental substring matches (e.g. colliding with
/// <c>http://ontopilot.localized/</c>) cannot happen.</param>
/// <param name="ToPrefix">Target prefix, e.g.
/// <c>"http://goodcrew.local/"</c>.</param>
/// <param name="DryRun">When <c>true</c>, count the rows that WOULD be
/// changed but do not write anything. Default <c>false</c>.</param>
public sealed record IriSqlOptions(
    string FromPrefix,
    string ToPrefix,
    bool DryRun = false);

/// <summary>
/// One column-update step. Records which table + column was rewritten
/// and how many rows were affected (or would have been, in dry-run
/// mode). The summary fields let the cutover gate verify that every
/// expected IRI-bearing column was touched.
/// </summary>
/// <param name="Table">EF Core entity table name.</param>
/// <param name="Column">Column that was rewritten via
/// <c>REPLACE(col, @from, @to)</c>.</param>
/// <param name="AffectedRows">Rows updated (or matching in dry-run
/// mode).</param>
public sealed record IriSqlColumnStep(string Table, string Column, long AffectedRows);

/// <summary>
/// Composite result of <see cref="IriSqlMigrator.MigrateAsync"/>. The
/// <see cref="Steps"/> list lets the cutover gate emit a per-column
/// audit; <see cref="TotalRowsChanged"/> is the single-number roll-up
/// the rehearsal manifest records.
/// </summary>
public sealed class IriSqlReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool DryRun { get; init; }
    public string FromPrefix { get; init; } = string.Empty;
    public string ToPrefix { get; init; } = string.Empty;
    public List<IriSqlColumnStep> Steps { get; } = new();

    public long TotalRowsChanged => Steps.Sum(s => s.AffectedRows);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
    }
}

/// <summary>
/// Rewrites every IRI-bearing string column in the .NET / EF Core
/// schema so the data layer aligns with the new
/// <c>OnToPilotOptions.IriRoot</c> / <c>VocabNamespace</c> defaults.
///
/// <para>Three categories of column are touched:
/// <list type="bullet">
///   <item><b>Named-graph IRIs</b> &mdash; <c>knowledge_systems.graph_iri</c>,
///   <c>base_iri</c>, and the three columns on
///   <c>release_deployment</c> (<c>tbox_graph_iri</c>,
///   <c>vocabulary_graph_iri</c>, <c>abox_graph_iri</c>).</item>
///   <item><b>Entity / property IRIs</b> &mdash; <c>entity_resolution</c>
///   (<c>class_iri</c>, <c>individual_iri</c>),
///   <c>tbox_reconciliation</c> / <c>validation_decision</c>
///   (<c>property_iri</c>).</item>
///   <item><b>Canonical-key strings</b> &mdash; <c>abox_provenance.fact_key</c>
///   (IRI is embedded in the <c>ind\|...</c> / <c>data\|...</c> prefix,
///   so we run <c>REPLACE()</c> on the raw string instead of relying on
///   the EF mapper).</item>
/// </list>
/// </para>
///
/// <para>All updates run via <c>ExecuteSqlRaw</c> with parameterised
/// <c>@from</c> / <c>@to</c> so the SQL is database-portable (SQLite +
/// PostgreSQL both implement <c>REPLACE()</c> with the same two-argument
/// signature; the EF Core provider translates <c>FormattableString</c>
/// parameters correctly on both).</para>
///
/// <para><b>Idempotence.</b> Running <see cref="MigrateAsync"/> a second
/// time after a successful first run is a no-op &mdash; every row that
/// contains the <c>FromPrefix</c> was already rewritten to
/// <c>ToPrefix</c> on the first pass. The per-column row counts return
/// zero on re-runs, which the cutover gate treats as the steady-state
/// signal.</para>
///
/// <para><b>Rollback.</b> Per user decision this migrator is one-way;
/// the cutover runbook does not implement a SQL-side reverse REPLACE.
/// To roll back, revert the .NET <c>OnToPilotOptions.IriRoot</c> /
/// <c>VocabNamespace</c> to their pre-migration values and redeploy;
/// the cutover rehearsal MUST succeed before the live cutover so we
/// never have to invoke rollback in anger.</para>
/// </summary>
public sealed class IriSqlMigrator
{
    private readonly OnToPilotDbContext _db;
    private readonly ILogger<IriSqlMigrator> _logger;

    /// <summary>
    /// The table + column pairs to rewrite. Order matters only for
    /// log readability &mdash; the SQL statements are independent.
    /// <para>
    /// Table / column spellings mirror the EF Core
    /// <c>IEntityTypeConfiguration</c> mappings in
    /// <c>src/OnToPilot/Infrastructure/Persistence/Configurations/EntityConfigurations.cs</c>:
    /// tables are <c>lowercase-no-separator</c> (e.g. <c>knowledgesystem</c>)
    /// and columns keep the PascalCase property name verbatim unless a
    /// configuration explicitly calls <c>HasColumnName</c> (the only one
    /// we touch here is <c>legacy_id</c> on each row, which is unchanged
    /// because no IRI column is stored there).
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string Column)> ColumnsToRewrite =
    new (string, string)[]
    {
        ("knowledgesystem", "GraphIri"),
        ("knowledgesystem", "BaseIri"),
        ("releasedeployment", "TboxGraphIri"),
        ("releasedeployment", "VocabularyGraphIri"),
        ("releasedeployment", "AboxGraphIri"),
        ("entityresolution", "ClassIri"),
        ("entityresolution", "IndividualIri"),
        ("tboxreconciliation", "PropertyIri"),
        ("validationdecision", "PropertyIri"),
        ("aboxprovenance", "FactKey"),
    };

    /// <summary>
    /// Public read-only view of <see cref="ColumnsToRewrite"/> so
    /// <see cref="IriSqlVerifier"/> verifies the same column set the
    /// migrator touched, without restating the tuple list. Drift
    /// between the two surfaces would silently invalidate the
    /// smoke-check (false-positive guard lost), so the verifier
    /// intentionally consumes this accessor rather than carrying its
    /// own copy.
    /// </summary>
    public static IReadOnlyList<(string Table, string Column)> ColumnsToRewritePublic
        => ColumnsToRewrite;

    public IriSqlMigrator(OnToPilotDbContext db, ILogger<IriSqlMigrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Run the SQL rewrite against every IRI-bearing column. Dry-run
    /// reports the would-be row counts without writing; a real run
    /// commits each column and returns a populated
    /// <see cref="IriSqlReport"/>.
    /// </summary>
    public async Task<IriSqlReport> MigrateAsync(IriSqlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidatePrefixes(options.FromPrefix, options.ToPrefix);

        var report = new IriSqlReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            DryRun = options.DryRun,
            FromPrefix = options.FromPrefix,
            ToPrefix = options.ToPrefix,
        };

        foreach (var (table, column) in ColumnsToRewrite)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 表/列名走 C# 字符串内插编译成 SQL 字面量(来自静态 ColumnsToRewrite
            // 元组,不是用户输入);值走 {0}/{1}/{2} positional placeholder,由
            // EF Core 绑定为 Npgsql 参数。SQL injection surface 仅限
            // FromPrefix / ToPrefix(均参数化)。
            // 为什么不用 ExecuteSqlInterpolatedAsync:它的 FormattableString
            // overload 会把每个 {...} 都当成 hole 转成 @p0..@pN,导致
            // table/column 也被参数化,PG 报 42P01: relation "@p0"。
            var likePattern = options.FromPrefix + "%";
            long affected;
            if (options.DryRun)
            {
                // EF1002: SqlQueryRaw<int> 故意把 table/column 作为字面量
                // 拼接(来自静态 ColumnsToRewrite 元组);前缀走 positional
                // {0} 参数。
#pragma warning disable EF1002
                var countResult = await _db.Database
                    .SqlQueryRaw<int>(
                        $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\" WHERE \"{column}\" LIKE {{0}} || '%'",
                        likePattern)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002
                affected = countResult.Count > 0 ? countResult[0] : 0;
            }
            else
            {
                // EF1002: ExecuteSqlRawAsync 同样把 table/column 作为字面量
                // 拼接(来自静态 ColumnsToRewrite 元组);前缀走 positional
                // {0}/{1}/{2} 参数。与 dry-run 分支对称抑制。
#pragma warning disable EF1002
                affected = await _db.Database
                    .ExecuteSqlRawAsync(
                        $"UPDATE \"{table}\" SET \"{column}\" = REPLACE(\"{column}\", {{0}}, {{1}}) WHERE \"{column}\" LIKE {{2}}",
                        new object[] { options.FromPrefix, options.ToPrefix, likePattern },
                        cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002
            }

            report.Steps.Add(new IriSqlColumnStep(table, column, affected));
            _logger.LogInformation(
                "{Mode} {Table}.{Column}: {Affected} row(s)",
                options.DryRun ? "[dry-run]" : "[apply]",
                table, column, affected);
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        return report;
    }

    private static void ValidatePrefixes(string fromPrefix, string toPrefix)
    {
        if (string.IsNullOrEmpty(fromPrefix))
        {
            throw new ArgumentException("FromPrefix must be non-empty.", nameof(fromPrefix));
        }
        if (string.IsNullOrEmpty(toPrefix))
        {
            throw new ArgumentException("ToPrefix must be non-empty.", nameof(toPrefix));
        }
        // Anchor on a path / fragment separator so the REPLACE cannot
        // hit a stray substring (e.g. an unrelated string column that
        // happens to contain the from prefix as a suffix).
        if (!(fromPrefix.EndsWith('/') || fromPrefix.EndsWith('#')))
        {
            throw new ArgumentException(
                $"FromPrefix must end with '/' or '#' (got '{fromPrefix}'). "
                + "This guards against substring collisions like 'http://ontopilot.localized/...'.",
                nameof(fromPrefix));
        }
        if (!(toPrefix.EndsWith('/') || toPrefix.EndsWith('#')))
        {
            throw new ArgumentException(
                $"ToPrefix must end with '/' or '#' (got '{toPrefix}').",
                nameof(toPrefix));
        }
    }
}
