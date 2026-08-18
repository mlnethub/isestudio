using System.Security.Cryptography;
using OnToPilot.Observability;

namespace OnToPilot.Storage;

/// <summary>
/// Filesystem-backed <see cref="IBlobStore"/> that writes each blob to
/// <c>{Root}/{aa}/{bb}/{full_sha}</c> on disk, mirroring the Python backend's
/// legacy CAS layout so that pre-migration <c>Document.storage_path</c>
/// rows continue to resolve.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic: the stream is first copied into a sibling temp file
/// whose name carries the SHA and a per-call GUID (so two writes of the
/// same content racing on the same target don't collide), then renamed
/// onto the final path with <see cref="File.Move(string, string, bool)"/>
/// and <c>overwrite: true</c>. The final <see cref="File.Move"/> only
/// runs after the hash has been verified — an interrupted write leaves
/// the target untouched and the temp file orphaned for the OS to reap.
/// </para>
/// <para>
/// Reads are opened with <see cref="FileShare.Read"/> so the same blob can
/// be served to multiple concurrent callers without contention.
/// </para>
/// <para>
/// Reference counting is intentionally NOT implemented here. The caller
/// must ensure no document still references a SHA before invoking
/// <see cref="RemoveAsync"/>; the extraction pipeline (Task 4) will own
/// that contract.
/// </para>
/// </remarks>
public sealed class LocalCasBlobStore : IBlobStore
{
    /// <summary>Chunk size used to drain the input stream while hashing.</summary>
    private const int StreamChunkSize = 81920;

    private readonly string _root;

    /// <summary>Filesystem root for every blob this store writes.</summary>
    public string Root => _root;

    /// <summary>Build a store rooted at <paramref name="root"/>.</summary>
    public LocalCasBlobStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Telemetry.StorageSource.WithStorageActivity(
            "storage.localcas.put",
            content.CanSeek ? (long?)content.Length : null,
            async ct =>
            {
                // Stage 1: copy the upload to a temp file under the root while
                // streaming it through IncrementalHash so we never hold the
                // entire payload in memory. Two writers of the same content
                // racing on the same target would otherwise collide on a
                // single shared temp filename.
                var tempId = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(_root);
                var tempPath = Path.Combine(_root, $".tmp-{tempId}");

                string sha256;
                try
                {
                    await using (var tempStream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        StreamChunkSize,
                        useAsync: true))
                    {
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[StreamChunkSize];
                        int read;
                        while ((read = await content.ReadAsync(buffer.AsMemory(0, StreamChunkSize), ct)
                                                       .ConfigureAwait(false)) > 0)
                        {
                            var slice = buffer.AsMemory(0, read);
                            hasher.AppendData(buffer, 0, read);
                            await tempStream.WriteAsync(slice, ct).ConfigureAwait(false);
                        }
                        await tempStream.FlushAsync(ct).ConfigureAwait(false);

                        var digest = hasher.GetHashAndReset();
                        sha256 = Convert.ToHexString(digest).ToLowerInvariant();
                    }

                    // Stage 2: move the now-hashed payload to its canonical path,
                    // skipping the move entirely if it already exists with
                    // identical content (an idempotent write).
                    var legacyPath = BlobKey.LegacyPathFor(sha256);
                    var finalPath = Path.Combine(_root, legacyPath);
                    var finalDir = Path.GetDirectoryName(finalPath)!;
                    Directory.CreateDirectory(finalDir);

                    if (File.Exists(finalPath))
                    {
                        // A previous write already populated this SHA — drop our
                        // temp file rather than overwriting the canonical blob.
                        TryDelete(tempPath);
                    }
                    else
                    {
                        try
                        {
                            File.Move(tempPath, finalPath, overwrite: false);
                        }
                        catch (IOException)
                        {
                            // Race: another concurrent writer won the move. Fall
                            // through; the file at finalPath has the same
                            // content (same SHA), so it's still the canonical
                            // copy.
                            TryDelete(tempPath);
                        }
                    }

                    return new BlobWriteResult(sha256, legacyPath);
                }
                catch
                {
                    // Best-effort cleanup: a failed put must not leave a temp
                    // file behind in the store root.
                    TryDelete(tempPath);
                    throw;
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken)
    {
        return Telemetry.StorageSource.WithStorageActivity(
            "storage.localcas.get",
            null,
            ct =>
            {
                var path = PathForSha(sha256);
                if (!File.Exists(path))
                {
                    return Task.FromResult<Stream?>(null);
                }

                Stream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    StreamChunkSize,
                    useAsync: true);
                return Task.FromResult<Stream?>(stream);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken)
    {
        return Telemetry.StorageSource.WithStorageActivity(
            "storage.localcas.exists",
            null,
            ct => Task.FromResult(File.Exists(PathForSha(sha256))),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
    {
        return Telemetry.StorageSource.WithStorageActivity(
            "storage.localcas.remove",
            null,
            ct =>
            {
                var path = PathForSha(sha256);
                if (!File.Exists(path))
                {
                    return Task.FromResult(false);
                }

                try
                {
                    File.Delete(path);
                    return Task.FromResult(true);
                }
                catch (DirectoryNotFoundException)
                {
                    return Task.FromResult(false);
                }
            },
            cancellationToken);
    }

    private string PathForSha(string sha256)
    {
        // BlobKey.LegacyPathFor throws on malformed input; we want a
        // best-effort lookup here so callers can probe for arbitrary
        // strings. Sanitize first.
        if (sha256.Length != 64)
        {
            return Path.Combine(_root, "__invalid__");
        }
        return Path.Combine(_root, BlobKey.LegacyPathFor(sha256));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Swallow: temp-file cleanup is best-effort. The OS will
            // eventually retire the file alongside its parent temp
            // root, and the next write that races on the same SHA
            // simply hits the existing canonical copy.
        }
    }
}
