using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISEStudio.Documents;

internal sealed class DocumentBlobReferenceLock : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> LocalLocks = new();

    private readonly NpgsqlConnection? _connection;
    private readonly long[] _keys;
    private readonly SemaphoreSlim[] _semaphores;

    private DocumentBlobReferenceLock(NpgsqlConnection connection, long[] keys)
    {
        _connection = connection;
        _keys = keys;
        _semaphores = [];
    }

    private DocumentBlobReferenceLock(long[] keys, SemaphoreSlim[] semaphores)
    {
        _keys = keys;
        _semaphores = semaphores;
    }

    public static async Task<DocumentBlobReferenceLock> AcquireAsync(
        ISEStudioDbContext db,
        IEnumerable<string> sha256Values,
        CancellationToken cancellationToken)
    {
        var keys = sha256Values.Select(ToLockKey).Distinct().Order().ToArray();
        if (keys.Length == 0)
            throw new ArgumentException("At least one blob SHA-256 is required.", nameof(sha256Values));

        if (db.Database.IsNpgsql())
        {
            var connectionString = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("A PostgreSQL connection string is required for blob locking.");
            var dedicatedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString;
            var connection = new NpgsqlConnection(dedicatedConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var key in keys)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@lock_key)", connection);
                    command.Parameters.AddWithValue("lock_key", key);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                return new DocumentBlobReferenceLock(connection, keys);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var semaphores = keys.Select(key => LocalLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1))).ToArray();
        var acquired = 0;
        try
        {
            for (; acquired < semaphores.Length; acquired++)
                await semaphores[acquired].WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentBlobReferenceLock(keys, semaphores);
        }
        catch
        {
            for (var index = acquired - 1; index >= 0; index--)
                semaphores[index].Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                foreach (var key in _keys.Reverse())
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key)", _connection);
                    command.Parameters.AddWithValue("lock_key", key);
                    await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        for (var index = _semaphores.Length - 1; index >= 0; index--)
            _semaphores[index].Release();
    }

    private static long ToLockKey(string sha256)
    {
        var digest = SHA256.HashData(Convert.FromHexString(sha256));
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}

internal sealed class StagedBlobUpload : IAsyncDisposable
{
    private const int BufferSize = 81920;
    private readonly string _path;

    private StagedBlobUpload(string path, string sha256, long sizeBytes)
    {
        _path = path;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    public string Sha256 { get; }
    public long SizeBytes { get; }

    public static async Task<StagedBlobUpload> CreateAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Path.Combine(Path.GetTempPath(), $"isestudio-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long sizeBytes = 0;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sizeBytes += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (sizeBytes == 0)
                throw new InvalidOperationException("Empty file");
            var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return new StagedBlobUpload(path, sha256, sizeBytes);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public Stream OpenRead()
        => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public ValueTask DisposeAsync()
    {
        TryDelete(_path);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}