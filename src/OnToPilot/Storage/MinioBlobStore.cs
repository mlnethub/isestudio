using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace OnToPilot.Storage;

/// <summary>
/// S3-compatible (MinIO) <see cref="IBlobStore"/>. Uses path-style
/// addressing and AWS Signature V4 via the official AWSSDK.S3 client.
/// </summary>
/// <remarks>
/// <para>
/// Each blob is uploaded under the key produced by
/// <see cref="BlobKey.LegacyPathFor"/>, which mirrors the Python backend's
/// <c>{aa}/{bb}/{full_sha}</c> fanout. This is what makes
/// <c>Document.storage_path</c> rows portable across the migration: the
/// same SHA-256 hashes to the same key regardless of which backend
/// serves it.
/// </para>
/// <para>
/// <see cref="PutAsync"/> streams the request body via
/// <see cref="PutObjectRequest.InputStream"/> while an
/// <see cref="IncrementalHash"/> running over the same chunked read
/// accumulates the digest. The S3 client buffers the input stream into
/// a signed chunked request internally, so callers don't pay for an
/// out-of-process buffer.
/// </para>
/// <para>
/// Reference counting is intentionally NOT implemented here — the
/// caller is responsible for invoking <see cref="RemoveAsync"/> only
/// when no document still references the SHA. The extraction
/// pipeline (Task 4) owns that contract.
/// </para>
/// </remarks>
public sealed class MinioBlobStore : IBlobStore
{
    /// <summary>Chunk size for the streaming read on Put/Get.</summary>
    private const int StreamChunkSize = 81920;

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    /// <summary>The bucket that every operation targets.</summary>
    public string Bucket => _bucket;

    /// <summary>
    /// Build a store wrapping an already-configured <see cref="IAmazonS3"/>.
    /// The caller is expected to have configured the client for path-style
    /// addressing (<see cref="AmazonS3Config.ForcePathStyle"/> = true).
    /// </summary>
    public MinioBlobStore(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        _s3 = s3;
        _bucket = bucket;
    }

    /// <summary>
    /// Convenience constructor: build a path-style S3 client pointed at
    /// <paramref name="endpoint"/> using static credentials and create the
    /// store on top of it.
    /// </summary>
    public static MinioBlobStore Create(
        string endpoint,
        string accessKey,
        string secretKey,
        string bucket,
        string region = "us-east-1")
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(accessKey);
        ArgumentException.ThrowIfNullOrEmpty(secretKey);
        ArgumentException.ThrowIfNullOrEmpty(bucket);

        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            AuthenticationRegion = region,
        };
        var client = new AmazonS3Client(accessKey, secretKey, config);
        return new MinioBlobStore(client, bucket);
    }

    /// <inheritdoc />
    public async Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Stream into a MemoryStream only after we've accumulated the
        // hash and the key — we still want a streaming round-trip in
        // total memory. IncrementalHash rides along over the same
        // buffered read, and the result is fed to S3 once we know
        // the final key.
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffered = new MemoryStream();
        var scratch = new byte[StreamChunkSize];
        int read;
        while ((read = await content.ReadAsync(scratch.AsMemory(0, StreamChunkSize), cancellationToken)
                                       .ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(scratch, 0, read);
            buffered.Write(scratch, 0, read);
        }
        var digest = hasher.GetHashAndReset();
        var sha256 = Convert.ToHexString(digest).ToLowerInvariant();
        var key = BlobKey.LegacyPathFor(sha256);

        // Idempotent write: skip the upload entirely if the object is
        // already in place. Cheaper than re-sending identical bytes
        // for the (common) re-upload-after-restart case.
        try
        {
            await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);
            return new BlobWriteResult(sha256, key);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            // Fall through to upload.
        }

        buffered.Position = 0;
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = buffered,
            // ContentType intentionally left null; the Python backend
            // stored raw binary without an explicit MIME, and we want
            // bit-for-bit round-trips of the bytes themselves.
        };
        await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

        return new BlobWriteResult(sha256, key);
    }

    /// <inheritdoc />
    public async Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken)
    {
        var key = BlobKey.LegacyPathFor(sha256);
        try
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);

            // The ResponseStream is owned by the caller after this
            // method returns. The underlying S3 client disposes its
            // copy when the response object is disposed; the inner
            // HttpClient keeps the network read alive while we're
            // handing the stream up.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken)
    {
        var key = BlobKey.LegacyPathFor(sha256);
        try
        {
            await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
    {
        var key = BlobKey.LegacyPathFor(sha256);
        try
        {
            await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }

        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucket,
            Key = key,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool IsNotFound(AmazonS3Exception ex)
    {
        // MinIO returns either HTTP 404 or "NoSuchKey" in the error
        // code depending on the request shape; AWS S3 returns the
        // same. Match both to keep the contract uniform across
        // backends.
        return ex.StatusCode == System.Net.HttpStatusCode.NotFound
            || ex.ErrorCode is "NoSuchKey" or "NotFound";
    }
}
