using System.Text.Json;

namespace OnToPilot.Migration.Blobs;

/// <summary>
/// File-backed <see cref="IBlobMigrationStateStore"/>. Stores the log as
/// a JSON document at <see cref="Path"/> so the resume path works across
/// process restarts without any external database dependency.
///
/// <para>Shape on disk:</para>
/// <code>
/// {
///   "entries": {
///     "&lt;sha256&gt;": { "uploadedUtc": "2026-08-16T12:00:00Z", "size": 1234, "verified": true }
///   }
/// }
/// </code>
///
/// <para>Concurrency: a process-wide <c>SemaphoreSlim</c> serialises
/// reads and writes against the same <see cref="Path"/>. The
/// single-process rehearsal / cutover is the only intended use so a
/// cross-process file lock is unnecessary; the semaphore keeps the
/// in-memory dict and the JSON file in sync when
/// <see cref="GetCompletedAsync"/> and <see cref="MarkCompletedAsync"/>
/// interleave within the same process.</para>
/// </summary>
public sealed class JsonBlobMigrationStateStore : IBlobMigrationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // camelCase so the on-disk shape matches the brief's documented
        // shape ({"entries": {"<sha>": {"uploadedUtc": "...", ...}}})
        // and so the file is human-friendly.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The on-disk path this store reads and writes.</summary>
    public string Path => _path;

    /// <summary>Build a store backed by <paramref name="path"/>. The file is created lazily on the first write.</summary>
    public JsonBlobMigrationStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, BlobMigrationState>> GetCompletedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, BlobMigrationState>(StringComparer.Ordinal);
            }

            await using var stream = File.OpenRead(_path);
            var doc = await JsonSerializer.DeserializeAsync<StateDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            if (doc?.Entries is null || doc.Entries.Count == 0)
            {
                return new Dictionary<string, BlobMigrationState>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, BlobMigrationState>(doc.Entries.Count, StringComparer.Ordinal);
            foreach (var (sha, state) in doc.Entries)
            {
                if (string.IsNullOrEmpty(sha) || state is null)
                {
                    continue;
                }
                result[sha] = state;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(string sha256, BlobMigrationState state, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateDocument doc;
            if (File.Exists(_path))
            {
                await using (var read = File.OpenRead(_path))
                {
                    doc = await JsonSerializer.DeserializeAsync<StateDocument>(read, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false) ?? new StateDocument();
                }
            }
            else
            {
                doc = new StateDocument();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path) ?? ".");
            }

            doc.Entries[sha256] = state;

            // Write atomically: stage to a temp file in the same directory
            // and rename. JSON serialisation can fail midway; leaving the
            // existing file in place on a failure is what makes the
            // resume path safe to re-attempt.
            var tmp = _path + ".tmp";
            await using (var write = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(write, doc, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>JSON-serialisable wrapper around the entries dictionary.</summary>
    private sealed class StateDocument
    {
        /// <summary>Map of lowercase-hex SHA-256 to per-blob state.</summary>
        public Dictionary<string, BlobMigrationState> Entries { get; init; } =
            new(StringComparer.Ordinal);
    }
}
