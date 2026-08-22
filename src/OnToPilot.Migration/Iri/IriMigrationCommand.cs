using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
                "rdf" => await RunRdfAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "shards" => await RunShardsAsync(rest, loggerFactoryScope).ConfigureAwait(false),
                "all" => await RunAllAsync(rest, loggerFactoryScope).ConfigureAwait(false),
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

    // -----------------------------------------------------------------
    // Argv parsing
    // -----------------------------------------------------------------

    private sealed record SqlCliArgs(
        string PostgresConnectionString,
        string FromPrefix,
        string ToPrefix,
        bool DryRun);

    private static SqlCliArgs? ParseSqlArgs(IReadOnlyList<string> argv)
    {
        string? pg = null;
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
                case "--postgres-connection-string": pg = Next(); break;
                case "--from-prefix": fromPrefix = Next() ?? fromPrefix; break;
                case "--to-prefix": toPrefix = Next() ?? toPrefix; break;
                case "--dry-run": dryRun = true; break;
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
        return new SqlCliArgs(pg, fromPrefix, toPrefix, dryRun);
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
