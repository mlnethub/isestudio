using ISEStudio.Migration.Blobs;
using ISEStudio.Migration.Iri;

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
            _ => Fail($"unknown subcommand '{subcommand}'"),
        };
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
        """;
}
