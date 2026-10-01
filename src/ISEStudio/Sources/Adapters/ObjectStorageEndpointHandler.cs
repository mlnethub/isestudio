using System.Globalization;
using System.Net.Http.Headers;

namespace ISEStudio.Sources.Adapters;

internal sealed class ObjectStorageEndpointHandler : DelegatingHandler
{
    private readonly string[] _hosts;
    private readonly ObjectStorageByteBudget _budget;

    public ObjectStorageEndpointHandler(params string[] hosts) : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        MaxResponseHeadersLength = 64,
    }, hosts) { }

    internal ObjectStorageEndpointHandler(HttpMessageHandler transport, params string[] hosts) : base(transport)
    {
        _hosts = hosts;
        _budget = new ObjectStorageByteBudget();
    }

    internal ObjectStorageEndpointHandler(HttpMessageHandler transport, ObjectStorageByteBudget budget, params string[] hosts) : base(transport)
    {
        _hosts = hosts;
        _budget = budget;
    }

    internal ObjectStorageEndpointHandler(ObjectStorageByteBudget budget, params string[] hosts) : this(hosts)
        => _budget = budget;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || !_hosts.Contains(uri.Host, StringComparer.Ordinal))
            throw new ObjectStorageSourceException("Object storage endpoint is not allowed.");
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new ObjectStorageSourceException("Object storage redirects are not allowed.");
            var headers = response.Headers.Concat(response.Content.Headers).ToArray();
            if (response.Content.Headers.TryGetValues("Content-Length", out var lengths)
                && (!long.TryParse(string.Join(",", lengths), NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
                    || declared > ObjectStorageSourceAdapter.MaxItemBytes || declared > _budget.Remaining))
                throw new ObjectStorageSourceException("Object storage response length is invalid or exceeds the limit.");
            if (headers.Sum(header => (long)header.Key.Length + header.Value.Sum(value => (long)value.Length)) > 65536
                || response.Content.Headers.ContentLength is < 0 or > ObjectStorageSourceAdapter.MaxItemBytes
                || response.Content.Headers.ContentLength > _budget.Remaining
                || (response.Content.Headers.TryGetValues("Content-Type", out var types)
                    && !MediaTypeHeaderValue.TryParse(string.Join(",", types), out _)))
                throw new ObjectStorageSourceException("Object storage response headers are invalid or exceed the limit.");
            var original = response.Content;
            var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bounded = new StreamContent(new ResponseStream(stream, original, _budget));
            foreach (var header in original.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = bounded;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private sealed class ResponseStream(Stream inner, HttpContent owner, ObjectStorageByteBudget budget) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        private int Allow(int count)
        {
            if (_read > ObjectStorageSourceAdapter.MaxItemBytes || budget.Remaining < 0)
                throw new ObjectStorageSourceException("Object storage response exceeds the byte limit.");
            return (int)Math.Min(count, Math.Min(ObjectStorageSourceAdapter.MaxItemBytes - _read, budget.Remaining) + 1);
        }
        private int Count(int count)
        {
            _read += count;
            budget.Consume(count);
            if (_read > ObjectStorageSourceAdapter.MaxItemBytes)
                throw new ObjectStorageSourceException("Object storage response exceeds the byte limit.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allow(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allow(buffer.Length)]));
        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0) Count(1);
            return value;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Count(await inner.ReadAsync(buffer, offset, Allow(count), cancellationToken).ConfigureAwait(false));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer[..Allow(buffer.Length)], cancellationToken).ConfigureAwait(false));
        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class ObjectStorageByteBudget
{
    private long _remaining = ObjectStorageSourceAdapter.MaxItemBytes;
    public long Remaining => Interlocked.Read(ref _remaining);
    public void Consume(long count)
    {
        if (Interlocked.Add(ref _remaining, -count) < 0)
            throw new ObjectStorageSourceException("Object storage scan exceeds the byte limit.");
    }
}