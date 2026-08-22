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
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string Column)> ColumnsToRewrite =
    new (string, string)[]
    {
        ("knowledge_systems", "graph_iri"),
        ("knowledge_systems", "base_iri"),
        ("release_deployment", "tbox_graph_iri"),
        ("release_deployment", "vocabulary_graph_iri"),
        ("release_deployment", "abox_graph_iri"),
        ("entity_resolution", "class_iri"),
        ("entity_resolution", "individual_iri"),
        ("tbox_reconciliation", "property_iri"),
        ("validation_decision", "property_iri"),
        ("abox_provenance", "fact_key"),
    };

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

            // REPLACE() is two-argument and database-portable; using
            // FromSqlInterpolated keeps @from / @to as parameterised
            // values so the SQL is injection-safe regardless of what
            // the prefix strings happen to contain.
            var sql = options.DryRun
                ? $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" LIKE {{0}} || '%'"
                : $"UPDATE \"{table}\" SET \"{column}\" = REPLACE(\"{column}\", {{0}}, {{1}}) WHERE \"{column}\" LIKE {{0}} || '%'";

            var fromParam = options.FromPrefix;
            var toParam = options.ToPrefix;
            var likePattern = fromParam + "%";

            long affected;
            if (options.DryRun)
            {
                // COUNT(*) query — returns the would-be row count. The
                // table/column are baked into the SQL string (they come
                // from the static ColumnsToRewrite list, never from user
                // input); the prefix argument flows in as a parameterised
                // {0} placeholder so the SQL is injection-safe.
                // EF1002 suppression is appropriate: the interpolated
                // {table} / {column} tokens are static compile-time
                // literals from the ColumnsToRewrite list above, not
                // runtime values, so the SQL injection surface is
                // limited to the parameterised {0} placeholder.
#pragma warning disable EF1002
                var countResult = await _db.Database
                    .SqlQueryRaw<int>(
                        $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\" WHERE \"{column}\" LIKE {{0}} || '%'",
                        fromParam)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore EF1002
                affected = countResult.Count > 0 ? countResult[0] : 0;
            }
            else
            {
                affected = await _db.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"{table}\" SET \"{column}\" = REPLACE(\"{column}\", {fromParam}, {toParam}) WHERE \"{column}\" LIKE {likePattern}",
                        cancellationToken)
                    .ConfigureAwait(false);
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
