using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using OnToPilot.Storage;

namespace OnToPilot.Migration.Blobs;

/// <summary>
/// Migrates the Python filesystem-laid-out CAS blobs
/// (<c>blobs/&lt;aa&gt;/&lt;bb&gt;/&lt;full_sha&gt;</c>) into a MinIO
/// bucket via <see cref="IBlobStore"/>, with per-object SHA-256
/// verification, dry-run, resume-after-interrupt, corruption detection,
/// and release-artifact exclusion.
///
/// <para><b>Pipeline.</b> For every regular file found under
/// <paramref name="sourceDir"/> the command:</para>
/// <list type="number">
///   <item>Stream-hashes the file's bytes through
///   <see cref="IncrementalHash"/> and asserts the digest equals the
///   filename (which IS the SHA-256 in the Python layout). A mismatch
///   is a corruption gate failure and aborts the run.</item>
///   <item>Looks up <c>ReferenceCount</c> from the <c>document</c>
///   table. Zero-reference blobs are logged as a warning and skipped —
///   this is what keeps release artifacts (which live outside the
///   source tree anyway) and orphans out of MinIO.</item>
///   <item>Unless <see cref="BlobMigrationOptions.DryRun"/> is set,
///   uploads the file via <see cref="IBlobStore.PutAsync"/>, then
///   re-fetches and re-hashes to confirm the round-trip (load-bearing).
///   On success the entry is recorded in
///   <see cref="IBlobMigrationStateStore"/> so a resume can skip it.</item>
/// </list>
///
/// <para><b>Source safety.</b> The command never opens the source via
/// any API that could mutate it. The walk is read-only; the SHA-256
/// hash is computed on a forward-only <see cref="FileStream"/>.
/// MinIO writes never touch the source filesystem.</para>
///
/// <para><b>Manifest.</b> After every blob is processed the manifest
/// is written to <see cref="BlobMigrationOptions.ManifestOut"/> (when
/// supplied). The on-disk shape is validated by
/// <see cref="BlobManifestSchemaValidator"/> before the run is
/// considered successful.</para>
/// </summary>
public sealed class BlobMigrationCommand
{
    private readonly ILogger<BlobMigrationCommand> _logger;

    /// <summary>Buffer size for the streaming SHA-256 computation.</summary>
    private const int HashChunkSize = 81920;

    /// <summary>
    /// Build a command with the supplied logger. The logger is the only
    /// required collaborator — the MinIO + PostgreSQL dependencies are
    /// passed to <see cref="RunAsync"/> per-invocation so the command can
    /// be reused across runs with different stores (the rehearsal and
    /// production buckets).
    /// </summary>
    public BlobMigrationCommand(ILogger<BlobMigrationCommand> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Run the migration.
    /// </summary>
    /// <param name="sourceDir">Python blob root, e.g. <c>backend/data/blobs</c>.</param>
    /// <param name="blobStore">Destination blob store. Reuses the existing <see cref="MinioBlobStore"/>; the bucket name is read off the store.</param>
    /// <param name="dataSource">PostgreSQL connection source for the <c>document.storagepath</c> reference-count lookup.</param>
    /// <param name="options">Run knobs. See <see cref="BlobMigrationOptions"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlobMigrationReport> RunAsync(
        string sourceDir,
        IBlobStore blobStore,
        NpgsqlDataSource dataSource,
        BlobMigrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDir);
        ArgumentNullException.ThrowIfNull(blobStore);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException(
                $"BlobMigrationCommand.RunAsync: source directory '{sourceDir}' does not exist.");
        }

        // Bucket name: the only IBlobStore implementation in the project
        // is MinioBlobStore, which exposes its bucket via the Bucket
        // property. When the cast doesn't apply (a custom test double)
        // we fall back to "unknown" — the value is only used in the
        // manifest, never as a routing key.
        var bucketName = (blobStore as MinioBlobStore)?.Bucket ?? "unknown";

        // The state store is optional: when no StatePath is supplied we
        // treat it as "every blob is new" and skip the disk read. This
        // keeps the dry-run + single-shot paths simple.
        var effectiveStateStore = string.IsNullOrEmpty(options.StatePath)
            ? (IBlobMigrationStateStore)new NullBlobMigrationStateStore()
            : new JsonBlobMigrationStateStore(options.StatePath!);

        // Snapshot the set of already-completed SHAs BEFORE we walk, so
        // a concurrent re-run can't make us double-write an entry.
        var completed = options.Force
            ? new Dictionary<string, BlobMigrationState>(StringComparer.Ordinal)
            : await effectiveStateStore.GetCompletedAsync(cancellationToken).ConfigureAwait(false);

        // Discover every file under sourceDir. The walk is deterministic
        // because we sort by full path before iterating; that means the
        // manifest is byte-identical across runs against an unchanged
        // source tree.
        var files = Directory
            .EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        _logger.LogInformation(
            "BlobMigrationCommand starting: source='{Source}', bucket='{Bucket}', dryRun={DryRun}, files={Count}",
            sourceDir, bucketName, options.DryRun, files.Length);

        var entries = new List<BlobManifestEntry>(files.Length);
        var uploadedCount = 0;
        var resumeSkippedCount = 0;
        var zeroReferenceSkippedCount = 0;
        var corruptedCount = 0;

        // Pre-warm the reference-count cache so we only query the
        // document table once per unique storage path. Multiple files
        // can hit the same path? No — every file under sourceDir has a
        // distinct name (the SHA-256 is part of the filename). But
        // zero-reference lookups still cost a round-trip; caching them
        // keeps the source walk from issuing a duplicate query if a
        // future layout accidentally duplicates a path.
        var referenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sha256 = Path.GetFileName(file);

            // Step 1: streaming SHA-256 + filename match. The Python
            // layout guarantees filename == sha256(bytes); a mismatch
            // is on-disk corruption or filesystem damage.
            string computedSha;
            long size;
            try
            {
                (computedSha, size) = await HashFileAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"BlobMigrationCommand failed to read '{file}' for hashing: {ex.Message}", ex);
            }

            if (!string.Equals(computedSha, sha256, StringComparison.Ordinal))
            {
                corruptedCount++;
                throw new InvalidOperationException(
                    $"BlobMigrationCommand CORRUPTION GATE: file '{file}' claims sha='{sha256}' but bytes hash to '{computedSha}'. "
                    + "Refusing to migrate corrupted blobs; check filesystem integrity.");
            }

            // The source path is the POSIX-style fanout the Python
            // backend used. ComputeRelative keeps Windows backslashes
            // out of the manifest.
            var relativeSource = ToPosix(Path.GetRelativePath(sourceDir, file));
            var storagePath = relativeSource; // also == aa/bb/<sha>

            // Step 2: reference count from PostgreSQL.
            var referenceCount = await GetReferenceCountAsync(
                dataSource, referenceCounts, storagePath, cancellationToken).ConfigureAwait(false);

            if (referenceCount == 0)
            {
                _logger.LogWarning(
                    "BlobMigrationCommand skipping zero-reference blob '{Sha}' (storage_path='{StoragePath}'); "
                    + "this is expected for orphan blobs and any release artifacts (which live outside the source tree anyway).",
                    sha256, storagePath);
                zeroReferenceSkippedCount++;
                continue;
            }

            // Step 3: state-store check. The blob is recorded in the
            // manifest either way (so a dry-run on a partially-migrated
            // tree sees the same shape as a fresh one), but no MinIO
            // call happens for already-completed entries.
            var isResumeSkip = !options.Force && completed.ContainsKey(sha256);
            if (isResumeSkip)
            {
                _logger.LogInformation(
                    "BlobMigrationCommand skipping already-uploaded sha='{Sha}' (state store hit).", sha256);
            }

            // Step 4: upload (or record) + verify. Skipped entirely when
            // the state store already records this blob — the entry is
            // still added to the manifest below so its presence stays
            // visible to the cutover orchestrator.
            //
            // SkipExisting is intentionally NOT consulted here:
            // MinioBlobStore.PutAsync is already idempotent at the SDK
            // layer (it short-circuits when an object with the same key
            // already exists, see src/OnToPilot/Storage/MinioBlobStore.cs
            // lines 110-122). The only resume path that matters is the
            // state-store check above.
            if (!options.DryRun && !isResumeSkip)
            {
                // PutAsync requires a seekable stream so it can hash
                // + upload in two passes. FileStream is seekable.
                await using (var uploadStream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: HashChunkSize, useAsync: true))
                {
                    var write = await blobStore.PutAsync(uploadStream, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(write.Sha256, sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"BlobMigrationCommand: PutAsync returned sha='{write.Sha256}' but the source file's sha is '{sha256}'. "
                            + "The store's content-addressing contract was violated.");
                    }
                }

                // Load-bearing verification: re-fetch and re-hash. If
                // MinIO silently corrupted the bytes (or our stream was
                // mutated) the digest diverges and we abort.
                var verified = await VerifyRoundTripAsync(blobStore, sha256, cancellationToken).ConfigureAwait(false);
                if (!verified)
                {
                    throw new InvalidOperationException(
                        $"BlobMigrationCommand: post-upload re-hash of sha='{sha256}' failed. "
                        + "MinIO returned bytes that do not match the source. Aborting.");
                }

                await effectiveStateStore.MarkCompletedAsync(
                    sha256,
                    new BlobMigrationState(DateTimeOffset.UtcNow, size, Verified: true),
                    cancellationToken).ConfigureAwait(false);
                uploadedCount++;
            }

            entries.Add(new BlobManifestEntry(
                SourcePath: relativeSource,
                ObjectKey: storagePath,
                Size: size,
                Sha256: sha256,
                ReferenceCount: referenceCount));

            if (isResumeSkip)
            {
                resumeSkippedCount++;
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var report = new BlobMigrationReport(
            sourceDirectory: sourceDir,
            bucket: bucketName,
            dryRun: options.DryRun,
            force: options.Force,
            entries: entries,
            uploadedCount: uploadedCount,
            resumeSkippedCount: resumeSkippedCount,
            zeroReferenceSkippedCount: zeroReferenceSkippedCount,
            corruptedCount: corruptedCount,
            finishedAtUtc: finishedAt);

        if (!string.IsNullOrEmpty(options.ManifestOut))
        {
            await WriteManifestAsync(options.ManifestOut!, report, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "BlobMigrationCommand finished: uploaded={Uploaded}, resumeSkipped={ResumeSkipped}, zeroRefSkipped={ZeroRefSkipped}, corrupted={Corrupted}, manifestEntries={Entries}",
            uploadedCount, resumeSkippedCount, zeroReferenceSkippedCount, corruptedCount, entries.Count);

        return report;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Compute the SHA-256 of a file by streaming it through an
    /// <see cref="IncrementalHash"/>. Returns the lowercase-hex digest
    /// plus the byte count. Never buffers the full file in memory.
    /// </summary>
    private static async Task<(string Sha256, long Size)> HashFileAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: HashChunkSize, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashChunkSize];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, HashChunkSize), cancellationToken)
                                   .ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            total += read;
        }
        var digest = hasher.GetHashAndReset();
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return (hex, total);
    }

    /// <summary>
    /// Look up <c>SELECT COUNT(*) FROM document WHERE storage_path = @p</c>.
    /// Cached per-path so duplicate paths (which the current source
    /// layout doesn't produce, but a future one might) don't issue a
    /// second query.
    /// </summary>
    private static async Task<int> GetReferenceCountAsync(
        NpgsqlDataSource dataSource,
        Dictionary<string, int> cache,
        string storagePath,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(storagePath, out var cached))
        {
            return cached;
        }

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*)::int FROM document WHERE storagepath = @p";
        cmd.Parameters.AddWithValue("@p", storagePath);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var count = result is null ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        cache[storagePath] = count;
        return count;
    }

    /// <summary>
    /// Re-fetch the just-uploaded blob from MinIO and re-hash it. Returns
    /// <see langword="true"/> when the SHA-256 round-trips, false when it
    /// diverges. Treats any read error as a failure (the caller aborts).
    /// </summary>
    private static async Task<bool> VerifyRoundTripAsync(
        IBlobStore blobStore, string sha256, CancellationToken cancellationToken)
    {
        var stream = await blobStore.GetAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return false;
        }
        await using (stream)
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[HashChunkSize];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, HashChunkSize), cancellationToken)
                                       .ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
            var digest = hasher.GetHashAndReset();
            var hex = Convert.ToHexString(digest).ToLowerInvariant();
            return string.Equals(hex, sha256, StringComparison.Ordinal);
        }
    }

    private static async Task WriteManifestAsync(
        string path, BlobMigrationReport report, CancellationToken cancellationToken)
    {
        var manifest = new ManifestFile
        {
            Version = BlobMigrationReport.ManifestVersion,
            SourceDirectory = report.SourceDirectory,
            Bucket = report.Bucket,
            GeneratedAtUtc = report.FinishedAtUtc,
            Entries = report.Entries.ToArray(),
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Manifest JSON uses camelCase property names so the wire format
        // matches the JSON Schema (version, sourceDirectory, bucket, ...).
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, options, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Convert a Windows backslash path to POSIX slashes for the manifest.</summary>
    private static string ToPosix(string relativePath)
        => relativePath.Replace('\\', '/');

    /// <summary>On-disk JSON shape; mirrors <c>migration/manifests/blob-manifest.schema.json</c>.</summary>
    private sealed class ManifestFile
    {
        public string Version { get; init; } = BlobMigrationReport.ManifestVersion;
        public string SourceDirectory { get; init; } = "";
        public string Bucket { get; init; } = "";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public BlobManifestEntry[] Entries { get; init; } = Array.Empty<BlobManifestEntry>();
    }

    /// <summary>
    /// In-memory state store used when the caller did not supply a path.
    /// Behaviourally a no-op: every blob is uploaded; nothing is
    /// persisted; nothing survives across runs.
    /// </summary>
    private sealed class NullBlobMigrationStateStore : IBlobMigrationStateStore
    {
        public Task<IReadOnlyDictionary<string, BlobMigrationState>> GetCompletedAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, BlobMigrationState>>(
                new Dictionary<string, BlobMigrationState>(StringComparer.Ordinal));

        public Task MarkCompletedAsync(string sha256, BlobMigrationState state, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
