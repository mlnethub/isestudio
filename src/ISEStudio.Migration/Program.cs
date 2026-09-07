using ISEStudio.Migration.Blobs;
using ISEStudio.Migration.Iri;
using ISEStudio.Migration.Rehearsal;

namespace ISEStudio.Migration;

/// <summary>
/// Console host entry point for the Migration assembly. Dispatches to
/// the per-data-layer command entry points by subcommand:
///
/// <list type="bullet">
///   <item><c>dotnet ISEStudio.Migration.dll blobs ...</c> — blob migration
///   (this task's primary deliverable).</item>
///   <item><c>dotnet ISEStudio.Migration.dll iri ...</c> — IRI prefix
///   migration (sql | rdf | shards | all subcommands).</item>
///   <item><c>--help</c> / <c>-h</c> — usage.</item>
/// </list>
///
/// <para>Other commands (SQL, RDF) are run as in-process APIs by Task 4's
/// orchestrator; they do not need CLI entry points because the
/// orchestrator owns the database / store lifecycle.</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Help);
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "blobs" => await BlobMigrationEntryPoint.RunAsync(rest).ConfigureAwait(false),
            "iri" => await IriMigrationCommand.RunAsync(rest).ConfigureAwait(false),
            "rehearsal" => await RunRehearsalAsync(rest).ConfigureAwait(false),
            _ => Fail($"unknown subcommand '{subcommand}'"),
        };
    }

    private static async Task<int> RunRehearsalAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(RehearsalUsage);
            return 0;
        }
        if (!TryParseRehearsalArgs(args, out var options))
        {
            Console.Error.WriteLine(RehearsalUsage);
            return 1;
        }

        try
        {
            var manifest = await new MigrationRehearsalCommand()
                .RunAsync(options!, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"[migration-rehearsal] mode={manifest.DatabaseMode} passed={manifest.Passed} manifest={options!.ManifestPath}");
            return manifest.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[migration-rehearsal] FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseRehearsalArgs(
        IReadOnlyList<string> args,
        out MigrationRehearsalOptions? options)
    {
        string? mode = null, manifest = null, postgres = null, backup = null;
        string? rdfSource = null, rdfCopy = null, rdfWork = null, blobManifest = null, expectedBackupSha256 = null;
        long? expectedBackupSize = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h") { options = null; return false; }
            if (i + 1 >= args.Count) { options = null; return false; }
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--mode": mode = value; break;
                case "--manifest": manifest = value; break;
                case "--postgres-connection-string": postgres = value; break;
                case "--backup": backup = value; break;
                case "--rdf-source": rdfSource = value; break;
                case "--rdf-copy": rdfCopy = value; break;
                case "--rdf-work": rdfWork = value; break;
                case "--blob-manifest": blobManifest = value; break;
                case "--expected-backup-sha256": expectedBackupSha256 = value; break;
                case "--expected-backup-size":
                    if (!long.TryParse(value, out var parsedSize) || parsedSize < 0) { options = null; return false; }
                    expectedBackupSize = parsedSize;
                    break;
                default: options = null; return false;
            }
        }

        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(manifest))
        {
            options = null;
            return false;
        }

        options = new MigrationRehearsalOptions(mode, postgres, manifest, backup,
            rdfSource, rdfCopy, rdfWork, blobManifest, expectedBackupSha256, expectedBackupSize);
        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[migration] {message}");
        Console.Error.WriteLine(Help);
        return 1;
    }

    private const string Help = """
        ISEStudio.Migration CLI:
          blobs ...   Run the blob migration (Task 3). Pass --help to see its arguments.
          iri ...     Run the IRI prefix migration (sql | rdf | shards | all).
                      Pass --help to see its arguments.
                    rehearsal   Run fresh, restored, or upgrade migration gates and write a JSON manifest.
                                            Required: --mode <fresh|restored|upgrade> --manifest <path>
                                                                --postgres-connection-string <s>
        """;

        private const string RehearsalUsage = """
                rehearsal usage:
                    --mode <fresh|restored|upgrade>
                    --manifest <path>
                    --postgres-connection-string <s>
                    --backup <path>                 required for restored mode
                    --expected-backup-sha256 <hex>  optional restored artifact digest
                    --expected-backup-size <bytes>  optional restored artifact size
                    --rdf-source <dir> --rdf-copy <dir> --rdf-work <dir>
                    --blob-manifest <path>
                """;
}
