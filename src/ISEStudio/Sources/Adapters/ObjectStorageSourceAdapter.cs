using System.Runtime.CompilerServices;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources.Adapters;

public sealed record ObjectStorageObject(string Key, long Size, string? Version, DateTimeOffset? LastModified);
public sealed record ObjectStoragePage(IReadOnlyList<ObjectStorageObject> Objects, string? NextToken);

public interface IObjectStorageSourceClient : IDisposable
{
    Task<ObjectStoragePage> ListAsync(string bucket, string prefix, string? token, CancellationToken ct);
    Task<string?> DownloadAsync(string bucket, ObjectStorageObject item, Stream destination, CancellationToken ct);
}

public abstract class ObjectStorageSourceAdapter(ISourceSecretProtector secrets, TimeProvider clock) : ISourceAdapter
{
    private const int MaxItems = 1000;
    private const int MaxPages = 1000;
    public const int MaxItemBytes = 20 * 1024 * 1024;
    public abstract string Kind { get; }
    protected ISourceSecretProtector Secrets { get; } = secrets;
    protected abstract Func<IObjectStorageSourceClient> Client(SourceEntity source, JsonElement config);

    public Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(source.Config);
            var config = document.RootElement;
            if (source.Kind != Kind || ObjectStorageSourceConfig.Validate(Kind, config) is not null)
                throw new ObjectStorageSourceException("Object storage config is invalid.");
            var create = Client(source, config);
            var bucket = config.GetProperty("bucket").GetString()!;
            var prefix = ObjectStorageSourceConfig.Text(config, "prefix");
            return Task.FromResult(new SourceScan(Enumerate(create, bucket, prefix, cancellationToken), true));
        }
        catch (ObjectStorageSourceException) { throw; }
        catch (Exception) { throw new ObjectStorageSourceException("Object storage config or credentials are invalid."); }
    }

    private async IAsyncEnumerable<SourceItem> Enumerate(Func<IObjectStorageSourceClient> create, string bucket,
        string prefix, CancellationToken discoveryToken, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, discoveryToken, deadline.Token);
        var token = linked.Token;
        using var client = CreateClient(create);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var budget = new ObjectStorageByteBudget();
        var failed = false;
        string? next = null;
        var pages = 0;
        do
        {
            CheckCancellation();
            if (++pages > MaxPages) throw new ObjectStorageSourceException("Object storage page limit exceeded.");
            var page = await Execute(() => client.ListAsync(bucket, prefix, next, token)).ConfigureAwait(false);
            foreach (var item in page.Objects)
            {
                CheckCancellation();
                var externalKey = $"{(Kind == "gcs" ? "gs" : "s3")}://{bucket}/{item.Key}";
                if (!ObjectStorageSourceConfig.ValidKey(item.Key) || !item.Key.StartsWith(prefix, StringComparison.Ordinal)
                    || externalKey.Length > 1024 || !keys.Add(item.Key))
                    throw new ObjectStorageSourceException("Object storage listing contains invalid or duplicate identities.");
                if (keys.Count > MaxItems) throw new ObjectStorageSourceException("Object storage item limit exceeded.");
                if (item.Size < 0 || item.Size > MaxItemBytes || item.LastModified is null || string.IsNullOrEmpty(item.Version))
                {
                    failed = true;
                    continue;
                }
                if (item.Size == 0 && item.Key.EndsWith('/')) continue;
                var content = new CappedMemoryStream(budget);
                string? mime = null;
                var downloaded = false;
                try
                {
                    mime = await Execute(() => client.DownloadAsync(bucket, item, content, token)).ConfigureAwait(false);
                    CheckCancellation();
                    if (content.Length == 0) throw new ObjectStorageSourceException("Object storage item is empty.");
                    content.Position = 0;
                    downloaded = true;
                }
                catch (ObjectStorageSourceException) { CheckCancellation(); failed = true; }
                finally { if (!downloaded) await content.DisposeAsync().ConfigureAwait(false); }
                if (downloaded)
                    yield return new SourceItem(externalKey,
                        SafeFilename(item.Key), mime ?? "application/octet-stream", content, item.LastModified);
            }
            next = page.NextToken;
            if (next is not null && (string.IsNullOrWhiteSpace(next) || !tokens.Add(next)))
                throw new ObjectStorageSourceException("Object storage pagination is incomplete or cyclic.");
        } while (next is not null);
        CheckCancellation();
        if (failed) throw new ObjectStorageSourceException("Object storage scan contains failed items.");

        void CheckCancellation()
        {
            ct.ThrowIfCancellationRequested();
            discoveryToken.ThrowIfCancellationRequested();
            if (token.IsCancellationRequested) throw new ObjectStorageSourceException("Object storage scan timed out.");
        }

        async Task<T> Execute<T>(Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception)
            {
                CheckCancellation();
                throw new ObjectStorageSourceException("Object storage request failed.");
            }
        }
    }

    private static IObjectStorageSourceClient CreateClient(Func<IObjectStorageSourceClient> create)
    {
        try { return create(); }
        catch (Exception) { throw new ObjectStorageSourceException("Object storage client could not be created."); }
    }

    private static string SafeFilename(string key)
    {
        var name = key.Split('/', '\\').Last();
        name = new string(name.Select(character => char.IsControl(character) || "<>:\"|?*".Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "object" : name;
    }

    private sealed class CappedMemoryStream(ObjectStorageByteBudget budget) : Stream
    {
        private readonly MemoryStream _content = new();
        public override bool CanRead => _content.CanRead;
        public override bool CanSeek => _content.CanSeek;
        public override bool CanWrite => _content.CanWrite;
        public override long Length => _content.Length;
        public override long Position { get => _content.Position; set => _content.Position = value; }
        public override void Flush() => _content.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _content.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _content.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _content.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _content.Dispose();
            base.Dispose(disposing);
        }
        private void Check(int count)
        {
            if (count > MaxItemBytes - Position) throw new ObjectStorageSourceException("Object storage item exceeds the byte limit.");
            budget.Consume(count);
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); _content.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); _content.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); _content.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Check(count); return _content.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Check(buffer.Length); return _content.WriteAsync(buffer, cancellationToken); }
        public override void SetLength(long value)
        {
            if (value > MaxItemBytes) throw new ObjectStorageSourceException("Object storage item exceeds the byte limit.");
            if (value > Length) budget.Consume(value - Length);
            _content.SetLength(value);
        }
    }
}