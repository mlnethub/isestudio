using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Migration.Iri;

/// <summary>
/// Per-subcommand output of <see cref="IriMigrationCommand.RunAsync"/>.
/// One section per migrator the dispatch actually invoked; fields are
/// null for subcommands the user did not request. The composite record
/// is what the cutover gate consumes to assert every required layer
/// ran.
/// </summary>
/// <param name="Sql">Result of <see cref="IriSqlMigrator"/>, or
/// <c>null</c> when the dispatch did not request the SQL
/// subcommand.</param>
/// <param name="Rdf">Result of <see cref="IriRdfRelocator"/>, or
/// <c>null</c> when the dispatch did not request the RDF
/// subcommand.</param>
/// <param name="Shards">Result of <see cref="IriShardRewriter"/>, or
/// <c>null</c> when the dispatch did not request the shards
/// subcommand.</param>
public sealed record IriMigrationOutput(
    IriSqlReport? Sql,
    IriRdfReport? Rdf,
    IriShardReport? Shards);

/// <summary>
/// CLI entry point for the IRI migration. Subcommands:
/// <list type="bullet">
///   <item><c>sql</c> &mdash; <see cref="IriSqlMigrator"/> rewrites the
///   IRI-bearing SQL columns.</item>
///   <item><c>rdf</c> &mdash; <see cref="IriRdfRelocator"/> exports the
///   source Oxigraph RocksDB store, rewrites every IRI, and writes a
///   fresh target store.</item>
///   <item><c>shards</c> &mdash; <see cref="IriShardRewriter"/>
///   rewrites the on-disk N-Quads shards + ks.json + manifest.json.</item>
///   <item><c>all</c> &mdash; run all three in order; any failure stops
///   the dispatch (the cutover runbook does NOT proceed past a
///   failure on a lower layer).</item>
/// </list>
///
/// <para>Invoked by Phase 2's PowerShell wrappers via
/// <c>dotnet OnToPilot.Migration.dll iri &lt;subcommand&gt; ...</c>.</para>
/// </summary>
public static class IriMigrationCommand
{
    public const string Usage = """
        IRI migration — rewrite http://ontopilot.local/ → http://goodcrew.local/.

        Subcommands:
          sql       Rewrite IRI-bearing SQL columns (knowledge_systems.graph_iri,
                    release_deployment.*_graph_iri, entity_resolution.{class,individual}_iri,
                    tbox_reconciliation/validation_decision.property_iri,
                    abox_provenance.fact_key).
                    Args:
                      --postgres-connection-string <s>   (required)
                      --from-prefix <prefix>              (default http://ontopilot.local/)
                      --to-prefix <prefix>                (default http://goodcrew.local/)
                      --dry-run                           (flag)

          sql-smoke-check
                    Verify that 'sql' actually rewrote every IRI-bearing column.
                    Counts residual rows whose value still starts with --from-prefix
                    and throws on any non-zero residual. Idempotent re-runs of 'sql'
                    report AffectedRows=0, which is vacuously true; this gate is
                    the proof. Read-only by construction.
                    Args:
                      --postgres-connection-string <s>   (required)
                      --from-prefix <prefix>              (default http://ontopilot.local/)
                      --to-prefix <prefix>                (default http://goodcrew.local/)
                      --report-out <path>                 (optional JSON report path)
                      --strict                            (also assert non-empty tables
                                                          contain at least one row with
                                                          the new prefix; catches the
                                                          failure mode where the migrator
                                                          removed the old prefix without
                                                          writing the new one)
                    Note: --dry-run is accepted but ignored (smoke-check is
                    inherently read-only).

          rdf       Relocate the live Oxigraph RocksDB store from the legacy
                    IRI prefix to the target one.
                    Args:
                      --source <dir>                      (required) source RocksDB
                      --target <dir>                      (required) target RocksDB (must not exist)
                      --from-prefix <prefix>              (default http://ontopilot.local/)
                      --to-prefix <prefix>                (default http://goodcrew.local/)

          shards    Rewrite on-disk N-Quads shards + ks.json + manifest.json
                    under the release + export roots.
                    Args:
                      --releases-root <dir>               (required)
                      --exports-root <dir>                (required)
                      --from-prefix <prefix>              (default http://ontopilot.local/)
                      --to-prefix <prefix>                (default http://goodcrew.local/)
                      --dry-run                           (flag)

          all       Run sql → rdf → shards. Stops on first failure.

          config    Print the resolved IRI configuration (IriRoot +
                    VocabNamespace) as JSON to stdout. Used by
                    Test-CrossStackParity.ps1 to verify the .NET
                    runtime reads the same env vars as Python.

        Common flags: --help / -h.
        """;

    /// <summary>
    /// Default prefixes — match the .NET <c>OnToPilotOptions.IriRoot</c>
    /// / <c>VocabNamespace</c> defaults introduced in Phase 0, so an
    /// operator who only passes the positional args gets the expected
    /// production cutover behaviour.
    /// </summary>
    public const string DefaultFromPrefix = "http://ontopilot.local/";
    public const string DefaultToPrefix = "http://goodcrew.local/";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args[0] is "--help" or "-h")
        {
            await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var loggerFactoryScope = loggerFactory;

        try
        {
            return subcommand switch
            {
                "sql" => await RunSqlAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "sql-smoke-check" => await RunSqlSmokeCheckAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "rdf" => await RunRdfAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "shards" => await RunShardsAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "all" => await RunAllAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "config" => await RunConfigAsync(rest).ConfigureAwait(false),
                _ => Fail($"unknown subcommand '{subcommand}'"),
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[iri-migration] FAILED: {ex.GetType().Name}: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Subcommand dispatch
    // -----------------------------------------------------------------

    private static async Task<int> RunSqlAsync(
        IReadOnlyList<string> argv,
        ILoggerFactory loggerFactory)
    {
        var parsed = ParseSqlArgs(argv);
        if (parsed is null) return 1;

        var sqlLogger = loggerFactory.CreateLogger<IriSqlMigrator>();
        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(parsed.PostgresConnectionString)
            .Options;
        await using var db = new OnToPilotDbContext(options);
        var migrator = new IriSqlMigrator(db, sqlLogger);
        var report = await migrator.MigrateAsync(
            new IriSqlOptions(parsed.FromPrefix, parsed.ToPrefix, parsed.DryRun),
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[iri-migration] sql: mode={0} totalRowsChanged={1} steps={2}",
            parsed.DryRun ? "dry-run" : "apply",
            report.TotalRowsChanged,
            report.Steps.Count));
        return 0;
    }

    /// <summary>
    /// Post-migration verification dispatch. Runs
    /// <see cref="IriSqlVerifier.VerifyAsync"/> against the same
    /// connection string and prefix pair the migrator was invoked
    /// with; optionally writes the JSON report + SHA-256 to
    /// <c>--report-out</c> for the cutover record audit trail. Read
    /// only; the <c>--dry-run</c> flag is accepted for symmetry with
    /// <c>iri sql</c> but has no effect.
    /// </summary>
    private static async Task<int> RunSqlSmokeCheckAsync(
        IReadOnlyList<string> argv,
        ILoggerFactory loggerFactory)
    {
        var parsed = ParseSqlArgs(argv);
        if (parsed is null) return 1;

        var verifyLogger = loggerFactory.CreateLogger<IriSqlVerifier>();
        var dbOptions = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(parsed.PostgresConnectionString)
            .Options;
        await using var db = new OnToPilotDbContext(dbOptions);
        var verifier = new IriSqlVerifier(db, verifyLogger);
        var report = await verifier.VerifyAsync(
            new IriSqlVerifyOptions(
                FromPrefix: parsed.FromPrefix,
                ToPrefix: parsed.ToPrefix,
                RequireNewPrefix: parsed.Strict),
            CancellationToken.None).ConfigureAwait(false);

        // report-out 是 audit trail:写盘后再算 SHA-256,这样
        // digest 与磁盘上的字节一一对应(cutover record 可直接引用)。
        if (!string.IsNullOrEmpty(parsed.ReportOut))
        {
            await report.WriteAsync(parsed.ReportOut, CancellationToken.None)
                .ConfigureAwait(false);
            var sha = IriSqlVerifyReport.ComputeReportSha256(parsed.ReportOut);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[iri-migration] sql-smoke-check: residualTotal={0} steps={1} reportPath={2} reportSha256={3}",
                report.ResidualTotal,
                report.Steps.Count,
                parsed.ReportOut,
                sha));
        }
        else
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[iri-migration] sql-smoke-check: residualTotal={0} steps={1} reportPath=(stdout-only)",
                report.ResidualTotal,
                report.Steps.Count));
        }
        return 0;
    }

    private static async Task<int> RunRdfAsync(
        IReadOnlyList<string> argv,
        ILoggerFactory loggerFactory)
    {
        var parsed = ParseRdfArgs(argv);
        if (parsed is null) return 1;

        var rdfLogger = loggerFactory.CreateLogger<IriRdfRelocator>();
        var relocator = new IriRdfRelocator(rdfLogger);
        var report = await relocator.RelocateAsync(
            new IriRdfOptions(
                SourcePath: parsed.Source,
                TargetPath: parsed.Target,
                FromPrefix: parsed.FromPrefix,
                ToPrefix: parsed.ToPrefix),
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[iri-migration] rdf: sourceQuads={0} targetQuads={1} sourceGraphs={2} targetGraphs={3} hash={4}",
            report.SourceQuadCount,
            report.TargetQuadCount,
            report.SourceNamedGraphs.Count,
            report.TargetNamedGraphs.Count,
            report.QuadSetHash));
        return 0;
    }

    private static async Task<int> RunShardsAsync(
        IReadOnlyList<string> argv,
        ILoggerFactory loggerFactory)
    {
        var parsed = ParseShardsArgs(argv);
        if (parsed is null) return 1;

        var shardsLogger = loggerFactory.CreateLogger<IriShardRewriter>();
        var rewriter = new IriShardRewriter(shardsLogger);
        var report = await rewriter.RewriteAsync(
            new IriShardOptions(
                ReleasesRoot: parsed.ReleasesRoot,
                ExportsRoot: parsed.ExportsRoot,
                FromPrefix: parsed.FromPrefix,
                ToPrefix: parsed.ToPrefix,
                DryRun: parsed.DryRun),
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[iri-migration] shards: mode={0} filesTouched={1} steps={2}",
            parsed.DryRun ? "dry-run" : "apply",
            report.FilesTouched,
            report.Steps.Count));
        return 0;
    }

    private static async Task<int> RunAllAsync(
        IReadOnlyList<string> argv,
        ILoggerFactory loggerFactory)
    {
        // Sequential, fail-fast — the cutover runbook does NOT proceed
        // past a lower-layer failure (sql must succeed before rdf; rdf
        // before shards) so we never end up with a half-migrated system.
        var sqlRc = await RunSqlAsync(argv, loggerFactory).ConfigureAwait(false);
        if (sqlRc != 0) return sqlRc;

        var rdfRc = await RunRdfAsync(argv, loggerFactory).ConfigureAwait(false);
        if (rdfRc != 0) return rdfRc;

        return await RunShardsAsync(argv, loggerFactory).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 3 cross-stack parity introspection. Builds an
    /// <see cref="IConfiguration"/> exactly the way the production
    /// host does (env vars + appsettings) and prints the resolved
    /// <see cref="OnToPilotOptions.IriRoot"/> / <see cref="OnToPilotOptions.VocabNamespace"/>
    /// as JSON to stdout. The Python Settings side reads the same
    /// <c>OnToPilot__IriRoot</c> / <c>OnToPilot__VocabNamespace</c>
    /// env vars via Pydantic, so <c>Test-CrossStackParity.ps1</c> can
    /// diff the two outputs and assert byte-identical IRI resolution.
    /// No side effects, no DB / store touched.
    /// </summary>
    private static async Task<int> RunConfigAsync(IReadOnlyList<string> argv)
    {
        // Build a configuration that mirrors the WebHost: env vars win
        // over appsettings.json defaults. We only need the two IRI
        // keys so we skip loading the full WebHost pipeline.
        _ = argv; // accepted for parity with the other subcommand signatures; no flags today
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new OnToPilotOptions();
        config.GetSection("OnToPilot").Bind(opts);

        // Emit canonical JSON (sorted keys, no whitespace) so the
        // parity diff is stable across PowerShell / .NET versions.
        var payload =
            "{" +
            "\"iri_root\":\"" + EscapeJson(opts.IriRoot) + "\"," +
            "\"vocab_namespace\":\"" + EscapeJson(opts.VocabNamespace) + "\"" +
            "}";
        await Console.Out.WriteLineAsync(payload).ConfigureAwait(false);
        return 0;
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // -----------------------------------------------------------------
    // Argv parsing
    // -----------------------------------------------------------------

    private sealed record SqlCliArgs(
        string PostgresConnectionString,
        string FromPrefix,
        string ToPrefix,
        bool DryRun,
        string? ReportOut,
        bool Strict);

    private static SqlCliArgs? ParseSqlArgs(IReadOnlyList<string> argv)
    {
        string? pg = null;
        var fromPrefix = DefaultFromPrefix;
        var toPrefix = DefaultToPrefix;
        var dryRun = false;
        string? reportOut = null;
        var strict = false;

        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            string? Next()
            {
                if (i + 1 >= argv.Count) return null;
                return argv[++i];
            }

            switch (a)
            {
                case "--help" or "-h": return null;
                case "--postgres-connection-string": pg = Next(); break;
                case "--from-prefix": fromPrefix = Next() ?? fromPrefix; break;
                case "--to-prefix": toPrefix = Next() ?? toPrefix; break;
                case "--dry-run": dryRun = true; break;
                // --report-out 只被 sql-smoke-check 使用;sql subcommand
                // 接受但忽略,保证两个子命令的 argv shape 一致。
                case "--report-out": reportOut = Next() ?? reportOut; break;
                // --strict 只被 sql-smoke-check 使用;sql subcommand 接受
                // 但忽略,与 --report-out / --dry-run 同样保持 argv shape
                // 对称(便于 ps1 包装层透传)。
                case "--strict": strict = true; break;
                default:
                    Console.Error.WriteLine($"[iri-migration] sql: unknown argument '{a}'");
                    return null;
            }
        }

        if (string.IsNullOrEmpty(pg))
        {
            Console.Error.WriteLine("[iri-migration] sql: --postgres-connection-string is required");
            return null;
        }
        return new SqlCliArgs(pg, fromPrefix, toPrefix, dryRun, reportOut, strict);
    }

    private sealed record RdfCliArgs(
        string Source,
        string Target,
        string FromPrefix,
        string ToPrefix);

    private static RdfCliArgs? ParseRdfArgs(IReadOnlyList<string> argv)
    {
        string? source = null, target = null;
        var fromPrefix = DefaultFromPrefix;
        var toPrefix = DefaultToPrefix;

        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            string? Next()
            {
                if (i + 1 >= argv.Count) return null;
                return argv[++i];
            }

            switch (a)
            {
                case "--help" or "-h": return null;
                case "--source": source = Next(); break;
                case "--target": target = Next(); break;
                case "--from-prefix": fromPrefix = Next() ?? fromPrefix; break;
                case "--to-prefix": toPrefix = Next() ?? toPrefix; break;
                default:
                    Console.Error.WriteLine($"[iri-migration] rdf: unknown argument '{a}'");
                    return null;
            }
        }

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
        {
            Console.Error.WriteLine("[iri-migration] rdf: --source and --target are required");
            return null;
        }
        return new RdfCliArgs(source, target, fromPrefix, toPrefix);
    }

    private sealed record ShardsCliArgs(
        string ReleasesRoot,
        string ExportsRoot,
        string FromPrefix,
        string ToPrefix,
        bool DryRun);

    private static ShardsCliArgs? ParseShardsArgs(IReadOnlyList<string> argv)
    {
        string? releases = null, exports = null;
        var fromPrefix = DefaultFromPrefix;
        var toPrefix = DefaultToPrefix;
        var dryRun = false;

        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            string? Next()
            {
                if (i + 1 >= argv.Count) return null;
                return argv[++i];
            }

            switch (a)
            {
                case "--help" or "-h": return null;
                case "--releases-root": releases = Next(); break;
                case "--exports-root": exports = Next(); break;
                case "--from-prefix": fromPrefix = Next() ?? fromPrefix; break;
                case "--to-prefix": toPrefix = Next() ?? toPrefix; break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine($"[iri-migration] shards: unknown argument '{a}'");
                    return null;
            }
        }

        if (string.IsNullOrEmpty(releases) || string.IsNullOrEmpty(exports))
        {
            Console.Error.WriteLine("[iri-migration] shards: --releases-root and --exports-root are required");
            return null;
        }
        return new ShardsCliArgs(releases, exports, fromPrefix, toPrefix, dryRun);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[iri-migration] {message}");
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
