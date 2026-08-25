using System.Security.Cryptography;
using ISEStudio.Storage;

namespace ISEStudio.Tests.Storage;

/// <summary>
/// Unit tests for the streaming primitives
/// (<see cref="HashingStream"/>, <see cref="ResponseOwningStream"/>) that
/// back <see cref="MinioBlobStore"/>. They run without Docker because the
/// wrappers are internal <see cref="System.IO.Stream"/> subclasses and can
/// be exercised directly with in-memory test streams.
/// </summary>
/// <remarks>
/// <para>
/// AWSSDK.S3's <c>IAmazonS3</c> interface exposes 158 methods, so a full
/// fake would dwarf the storage layer it tests. These wrapper tests cover
/// the I-1 and I-2 fixes at the layer where they live; the IBlobStore-level
/// behavior is exercised end-to-end by the Testcontainers-based integration
/// tests in <c>ISEStudio.IntegrationTests.Storage.MinioBlobStoreTests</c>.
/// </para>
/// </remarks>
public sealed class MinioBlobStoreTests
{
    /// <summary>
    /// Regression test for I-1: <see cref="HashingStream"/> must hash
    /// bytes as they pass through the wrapper, not buffer the payload.
    /// A metered source that yields 64 KB per Read drives the test:
    /// if the wrapper buffers, the source is read once and a 5 MB
    /// allocation happens. The hash accumulates on every forward
    /// read, so we expect many Read calls and the resulting SHA to
    /// match the SHA of the original buffer.
    /// </summary>
    [Fact]
    [Trait("Category", "Storage")]
    public async Task HashingStream_streams_without_buffering_entire_payload()
    {
        var bytes = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        var expectedSha = ComputeSha256Hex(bytes);

        using var metered = new MeteredStream(bytes, maxChunk: 64 * 1024);
        using var hashing = new HashingStream(metered, HashAlgorithmName.SHA256);

        // Drain the hashing stream — every Read on `hashing` forwards to
        // `metered` AND appends the bytes to the hasher. If the wrapper
        // buffered the payload internally, metered.ReadCount would be 1.
        var buffer = new byte[81920];
        int read;
        do
        {
            read = await hashing.ReadAsync(buffer.AsMemory(0, buffer.Length));
        } while (read > 0);

        Assert.True(
            metered.ReadCount > 1,
            $"Expected chunked reads from the source stream, got {metered.ReadCount} read(s). "
            + "If only one read happened, the hashing wrapper probably buffered the payload into memory.");
        Assert.Equal(bytes.Length, metered.TotalBytesRead);
        Assert.Equal(expectedSha, hashing.GetHashHexLower());
    }

    /// <summary>
    /// Regression test for I-2: <see cref="ResponseOwningStream"/> must
    /// dispose the parent response object when the returned stream is
    /// closed. The SDK pools responses, so a leak would starve the pool.
    /// </summary>
    [Fact]
    [Trait("Category", "Storage")]
    public async Task ResponseOwningStream_disposes_response_on_close()
    {
        var payload = "round-trip"u8.ToArray();
        var inner = new MemoryStream(payload);

        var owned = new TrackedDisposable();
        using var wrapper = new ResponseOwningStream(owned, inner);

        Assert.False(owned.IsDisposed);
        Assert.False(wrapper.IsResponseDisposed);

        var readBuffer = new byte[payload.Length];
        var read = await wrapper.ReadAsync(readBuffer.AsMemory());
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, readBuffer);

        Assert.False(
            owned.IsDisposed,
            "Disposing only via ReadAsync would already leak the pool slot; "
            + "the wrapper must wait for the caller's explicit Dispose.");

        wrapper.Dispose();

        Assert.True(
            owned.IsDisposed,
            "ResponseOwningStream did not dispose the parent response when the stream was closed; "
            + "this leaks the SDK's pooled connection slot.");
        Assert.True(wrapper.IsResponseDisposed);
    }

    /// <summary>
    /// Companion test: the wrapper's Close path (synchronous
    /// <see cref="Stream.Close"/>) must also dispose the response, since
    /// callers frequently close streams via either API.
    /// </summary>
    [Fact]
    [Trait("Category", "Storage")]
    public void ResponseOwningStream_disposes_response_on_sync_close()
    {
        var owned = new TrackedDisposable();
        var wrapper = new ResponseOwningStream(owned, new MemoryStream(new byte[] { 1, 2, 3 }));

        wrapper.Close();

        Assert.True(owned.IsDisposed);
        Assert.True(wrapper.IsResponseDisposed);
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>
/// Disposable test double that exposes its <see cref="IsDisposed"/> flag.
/// Used by <see cref="MinioBlobStoreTests.ResponseOwningStream_disposes_response_on_close"/>
/// to assert the wrapper correctly disposes the parent response.
/// </summary>
internal sealed class TrackedDisposable : IDisposable
{
    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// Forwarding stream that yields the underlying buffer in fixed-size
/// chunks, recording each <see cref="Read"/> / <see cref="ReadAsync"/>
/// call. Used to verify that <see cref="HashingStream"/> streams the
/// payload rather than buffering it.
/// </summary>
internal sealed class MeteredStream : Stream
{
    private readonly byte[] _buffer;
    private readonly int _maxChunk;
    private int _position;

    public MeteredStream(byte[] buffer, int maxChunk)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (maxChunk <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunk));
        _buffer = buffer;
        _maxChunk = maxChunk;
    }

    public int ReadCount { get; private set; }
    public long TotalBytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = (int)value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        var available = _buffer.Length - _position;
        if (available <= 0) return 0;
        var take = Math.Min(Math.Min(count, _maxChunk), available);
        Buffer.BlockCopy(_buffer, _position, buffer, offset, take);
        _position += take;
        TotalBytesRead += take;
        return take;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ReadCount++;
        var available = _buffer.Length - _position;
        if (available <= 0) return 0;
        var take = Math.Min(Math.Min(buffer.Length, _maxChunk), available);
        new ReadOnlySpan<byte>(_buffer, _position, take).CopyTo(buffer.Span);
        _position += take;
        TotalBytesRead += take;
        await Task.Yield();
        return take;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
    public override void SetLength(long value) => throw new NotImplementedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
}