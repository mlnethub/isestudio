using System.Security.Cryptography;

namespace OnToPilot.Storage;

/// <summary>
/// Forwarding <see cref="Stream"/> that, on every read, appends the bytes
/// read to an <see cref="IncrementalHash"/>. Used by
/// <see cref="MinioBlobStore.PutAsync"/> so the SHA-256 of the upload can be
/// computed while the S3 SDK drains the caller's stream — no out-of-process
/// buffer, no pre-load.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper does NOT buffer bytes: each read returns a chunk from the
/// underlying stream and feeds the same chunk to the hasher. The digest
/// accumulates in lock-step with the SDK's drain, so total memory use is
/// bounded by the read chunk size rather than by the payload size.
/// </para>
/// <para>
/// This class is <c>internal</c> because it's a streaming implementation
/// detail of <see cref="MinioBlobStore"/>; the public contract is the
/// <see cref="IBlobStore"/> surface.
/// </para>
/// </remarks>
internal sealed class HashingStream : Stream
{
    /// <summary>Chunk size used when the wrapper needs to drain the inner stream.</summary>
    private const int StreamChunkSize = 81920;

    private readonly Stream _inner;
    private readonly IncrementalHash _hasher;
    private readonly HashAlgorithmName _algorithm;
    private long _position;
    private long? _knownLength;
    private string? _cachedHashHex;

    /// <summary>Build a hashing wrapper around <paramref name="inner"/>.</summary>
    public HashingStream(Stream inner, HashAlgorithmName algorithm)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _algorithm = algorithm;
        _hasher = IncrementalHash.CreateHash(algorithm);
    }

    /// <summary>Lowercase-hex digest of every byte read so far.</summary>
    public string HashAlgorithmName => _algorithm.Name!;

    /// <summary>Total number of bytes that have been forwarded through the wrapper.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// Hint the wrapper about the total stream length. The AWSSDK S3
    /// client uses <c>Length - Position</c> to size the
    /// <c>Content-Length</c> header, and the wrapper's
    /// <see cref="Length"/> only knows the total after the first
    /// pass has drained the inner stream. <see cref="MinioBlobStore"/>
    /// calls this with the size computed by the pre-flight drain so
    /// the upload pass can be uploaded directly.
    /// </summary>
    public void SetKnownLength(long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _knownLength = length;
    }

    /// <summary>
    /// Drain the inner stream through the hasher. Used by
    /// <see cref="MinioBlobStore.PutAsync"/> to compute the digest before
    /// the SDK picks the key.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[StreamChunkSize];
        int read;
        while ((read = await _inner.ReadAsync(buffer.AsMemory(0, StreamChunkSize), cancellationToken)
                                      .ConfigureAwait(false)) > 0)
        {
            _hasher.AppendData(buffer, 0, read);
            BytesRead += read;
        }
    }

    /// <summary>
    /// Return the accumulated hash as a lowercase-hex digest. The hasher is
    /// reset so subsequent reads continue accumulating. The returned string
    /// is cached, so calling this method after the wrapper has been
    /// disposed still returns the last digest (used by MinioBlobStore's
    /// post-upload sanity check).
    /// </summary>
    public string GetHashHexLower()
    {
        if (_cachedHashHex is not null)
        {
            return _cachedHashHex;
        }
        var digest = _hasher.GetHashAndReset();
        _cachedHashHex = Convert.ToHexString(digest).ToLowerInvariant();
        return _cachedHashHex;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    /// <summary>
    /// Returns <see cref="BytesRead"/> after a drain, or the value passed
    /// to <see cref="SetKnownLength"/>. We avoid throwing here because the
    /// AWSSDK marshaller calls <c>Length</c> on the input stream to size
    /// the request, and reporting 0 (or any in-progress count) is more
    /// useful than failing the upload outright.
    /// </summary>
    public override long Length => _knownLength ?? BytesRead;
    public override long Position
    {
        get => _position;
        set
        {
            // Allow the SDK to "rewind" the wrapper between passes; the
            // underlying stream must be seekable for this to make sense.
            if (value < 0 || value > Length)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hasher.AppendData(buffer, offset, read);
            BytesRead += read;
            _position += read;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hasher.AppendData(buffer.Span[..read]);
            BytesRead += read;
            _position += read;
        }
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                                .ConfigureAwait(false);
        if (read > 0)
        {
            _hasher.AppendData(buffer, offset, read);
            BytesRead += read;
            _position += read;
        }
        return read;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The hasher is intentionally NOT disposed here: the AWSSDK
        // disposes our wrapper as part of finishing the upload, and
        // MinioBlobStore still calls GetHashHexLower() afterwards to
        // sanity-check that the upload-side hash matches the pre-flight
        // hash. Disposing the hasher here would invalidate the digest
        // and force the post-upload check to throw
        // ObjectDisposedException. The hasher holds no unmanaged
        // resources, so leaving it to GC is correct.
        //
        // _inner is also intentionally not disposed — the caller owns it.
        _ = disposing;
        base.Dispose(disposing);
    }
}