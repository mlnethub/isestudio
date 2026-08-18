using System.Globalization;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using Npgsql;
using OnToPilot.Storage;

namespace OnToPilot.Migration.Blobs;

/// <summary>
/// Console host for <see cref="BlobMigrationCommand"/>. Invoked by
/// <c>migration/scripts/Invoke-BlobMigration.ps1</c> via
/// <c>dotnet OnToPilot.Migration.dll blobs ...</c>.
///
/// <para>Library consumers (the integration tests, the API host when
/// running the migration in-process) call
/// <see cref="BlobMigrationCommand.RunAsync"/> directly and do not go
/// through this entry point.</para>
/// </summary>
public static class BlobMigrationEntryPoint
{
    /// <summary>
    /// Run a migration from the command line. Returns the process exit
    /// code: 0 on success, non-zero on failure (caller can surface this
    /// to PowerShell via <c>$LASTEXITCODE</c>).
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on success; 1 on any failure path.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsed = BlobMigrationCliArgs.Parse(args);
        if (parsed is null)
        {
            await Console.Error.WriteLineAsync(BlobMigrationCliArgs.Usage).ConfigureAwait(false);
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<BlobMigrationCommand>();

        var s3Config = new AmazonS3Config
        {
            ServiceURL = parsed.MinioEndpoint,
            ForcePathStyle = true,
            UseHttp = parsed.MinioEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            AuthenticationRegion = string.IsNullOrEmpty(parsed.MinioRegion) ? "us-east-1" : parsed.MinioRegion,
        };
        var s3 = new AmazonS3Client(parsed.MinioAccessKey, parsed.MinioSecretKey, s3Config);
        var blobStore = new MinioBlobStore(s3, parsed.Bucket);

        await using var dataSource = new NpgsqlDataSourceBuilder(parsed.PostgresConnectionString).Build();

        var cmd = new BlobMigrationCommand(logger);
        var options = new BlobMigrationOptions(
            DryRun: parsed.DryRun,
            Force: parsed.Force,
            SkipExisting: parsed.SkipExisting,
            ManifestOut: parsed.ManifestOut,
            StatePath: parsed.StatePath);

        try
        {
            var report = await cmd.RunAsync(
                parsed.Source, blobStore, dataSource, options, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[blob-migration] uploaded={0} skipped={1} corrupted={2} entries={3}",
                report.UploadedCount, report.SkippedCount, report.CorruptedCount, report.Entries.Count));
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[blob-migration] FAILED: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            s3.Dispose();
        }
    }
}

/// <summary>
/// Parsed CLI arguments for <see cref="BlobMigrationEntryPoint"/>.
/// Kept tiny so the PowerShell wrapper has a single, predictable shape
/// to drive.
/// </summary>
internal sealed record BlobMigrationCliArgs(
    string Source,
    string Bucket,
    string MinioEndpoint,
    string MinioAccessKey,
    string MinioSecretKey,
    string? MinioRegion,
    string PostgresConnectionString,
    string? ManifestOut,
    string? StatePath,
    bool DryRun,
    bool Force,
    bool SkipExisting)
{
    public const string Usage = """
        Invoke-BlobMigration.ps1 -> BlobMigrationEntryPoint usage:
          blob
            --source <dir>                  (required) Python blob root, e.g. backend/data/blobs
            --bucket <name>                 (required) MinIO bucket
            --minio-endpoint <url>          (required) e.g. http://127.0.0.1:9000
            --minio-access-key <key>        (required)
            --minio-secret-key <key>        (required)
            --minio-region <region>         (optional, default us-east-1)
            --postgres-connection-string <s>(required) Npgsql connection string
            --manifest-out <path>           (optional) write the JSON manifest here
            --state-path <path>             (optional) resume log path
            --dry-run                       (flag) do not upload
            --force                         (flag) ignore resume log
            --skip-existing                 (flag, default true) skip blobs already in MinIO
        """;

    /// <summary>
    /// Parse argv into a <see cref="BlobMigrationCliArgs"/>. Returns
    /// <see langword="null"/> when a required argument is missing or the
    /// caller passed <c>--help</c> / <c>-h</c>.
    /// </summary>
    public static BlobMigrationCliArgs? Parse(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        string? source = null, bucket = null, endpoint = null, accessKey = null, secretKey = null, region = null;
        string? pg = null, manifestOut = null, statePath = null;
        bool dryRun = false, force = false, skipExisting = true;

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
                case "--help" or "-h":
                    return null;
                case "--source": source = Next(); break;
                case "--bucket": bucket = Next(); break;
                case "--minio-endpoint": endpoint = Next(); break;
                case "--minio-access-key": accessKey = Next(); break;
                case "--minio-secret-key": secretKey = Next(); break;
                case "--minio-region": region = Next(); break;
                case "--postgres-connection-string": pg = Next(); break;
                case "--manifest-out": manifestOut = Next(); break;
                case "--state-path": statePath = Next(); break;
                case "--dry-run": dryRun = true; break;
                case "--force": force = true; break;
                case "--skip-existing": skipExisting = true; break;
                case "--no-skip-existing": skipExisting = false; break;
                default:
                    Console.Error.WriteLine($"[blob-migration] unknown argument: '{a}'");
                    return null;
            }
        }

        if (string.IsNullOrEmpty(source)
            || string.IsNullOrEmpty(bucket)
            || string.IsNullOrEmpty(endpoint)
            || string.IsNullOrEmpty(accessKey)
            || string.IsNullOrEmpty(secretKey)
            || string.IsNullOrEmpty(pg))
        {
            Console.Error.WriteLine("[blob-migration] missing required argument(s)");
            return null;
        }

        return new BlobMigrationCliArgs(
            source!, bucket!, endpoint!, accessKey!, secretKey!, region, pg!,
            manifestOut, statePath, dryRun, force, skipExisting);
    }
}
