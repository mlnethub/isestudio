namespace ISEStudio.Storage;

/// <summary>
/// Forwarding <see cref="Stream"/> that owns an
/// <see cref="IDisposable"/> parent: closing or disposing this stream
/// disposes the parent, which releases any SDK-managed pooled resources
/// (network connections, leases, etc.) attached to it.
/// </summary>
/// <remarks>
/// <para>
/// The AWSSDK S3 client pools <c>GetObjectResponse</c> objects. If the
/// caller forgets to dispose the stream returned by
/// <see cref="MinioBlobStore.GetAsync"/>, the parent response leaks and
/// the pool slot is never returned. This wrapper fixes that by tying
/// disposal of the stream to disposal of the response.
/// </para>
/// <para>
/// This class is <c>internal</c> because it's a streaming implementation
/// detail of <see cref="MinioBlobStore"/>; the public contract is the
/// <see cref="IBlobStore"/> surface.
/// </para>
/// </remarks>
internal sealed class ResponseOwningStream : Stream
{
    private readonly IDisposable _response;
    private readonly Stream _inner;
    private bool _disposed;

    /// <summary>
    /// Wrap <paramref name="inner"/> so that closing the returned stream
    /// also disposes <paramref name="response"/>.
    /// </summary>
    public ResponseOwningStream(IDisposable response, Stream inner)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(inner);
        _response = response;
        _inner = inner;
    }

    /// <summary>True once the wrapper and its owned response have been disposed.</summary>
    public bool IsResponseDisposed => _disposed;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _inner.Dispose();
            }
            finally
            {
                _response.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}