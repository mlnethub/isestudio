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
/// <see cref="PutAsync"/> streams the caller's stream through an internal
/// <see cref="HashingStream"/> wrapper that accumulates the SHA-256 in
/// the same chunked reads the SDK issues. The caller's bytes never
/// accumulate in process memory — bytes flow through hash and SDK
/// simultaneously as the SDK drains the wrapper.
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

        // Pre-flight hash: drain the caller's stream through a HashingStream
        // wrapper that accumulates the SHA-256 in-place as it forwards
        // reads. No MemoryStream buffer is allocated — bytes flow through
        // once. We need the SHA up-front because the S3 SDK requires the
        // object key to be set on PutObjectRequest before the HTTP request
        // goes out.
        using var hashing = new HashingStream(content, HashAlgorithmName.SHA256);
        await hashing.DrainAsync(cancellationToken).ConfigureAwait(false);
        var sha256 = hashing.GetHashHexLower();
        var totalBytes = hashing.BytesRead;
        var key = BlobKey.LegacyPathFor(sha256);

        // Idempotent write: skip the upload entirely if the object is
        // already in place. Cheaper than re-sending identical bytes for
        // the (common) re-upload-after-restart case.
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

        // Replay: wrap the caller's stream again and hand it to the SDK
        // so bytes flow through the hasher and the SDK read pipe in
        // lock-step. The stream must be seekable so we can rewind; HTTP
        // request bodies (and FileStream/MemoryStream) satisfy this.
        // We do not buffer — the second pass also reads without copying.
        if (!content.CanSeek)
        {
            throw new NotSupportedException(
                "MinioBlobStore.PutAsync requires a seekable stream so the payload can be hashed and uploaded in two passes; "
                + "pass a MemoryStream, FileStream, or an HTTP request body that ASP.NET has buffered to a seekable backing store.");
        }
        content.Position = 0;

        using var uploadHashing = new HashingStream(content, HashAlgorithmName.SHA256);
        // Pre-flight drain already gave us the total byte count. Pass
        // it to the upload wrapper so its Length property returns the
        // correct value (the SDK marshaller reads Length - Position to
        // size the request body).
        uploadHashing.SetKnownLength(totalBytes);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = uploadHashing,
            // HashingStream is not seekable, so the SDK can't compute
            // its default payload checksum by re-reading the stream.
            // Disable the default checksum path; the SDK still verifies
            // integrity via the HTTP body, and we have a stronger
            // application-level SHA on the stored object.
            DisableDefaultChecksumValidation = true,
            // ContentType intentionally left null; the Python backend
            // stored raw binary without an explicit MIME, and we want
            // bit-for-bit round-trips of the bytes themselves.
        };
        // The SDK requires Content-Length up-front. We know it from the
        // pre-flight drain (no extra buffering needed) so set it on the
        // Headers collection; otherwise the marshaller throws
        // "Could not determine content length" because HashingStream is
        // not a known-seekable type and the SDK can't auto-derive length.
        request.Headers.ContentLength = totalBytes;
        await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

        // Sanity-check: the upload pass must produce the same SHA we
        // named the key with. If they ever diverge, the caller's stream
        // was mutated between reads, which is a caller bug.
        var uploadSha = uploadHashing.GetHashHexLower();
        if (!string.Equals(uploadSha, sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SHA mismatch between pre-flight hash ({sha256}) and upload-time "
                + $"hash ({uploadSha}); the caller's stream was mutated between reads.");
        }

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

            // Wrap ResponseStream so that disposing the stream returned
            // to the caller also disposes the parent GetObjectResponse.
            // The SDK pools these response objects; leaking one starves
            // the pool. The wrapper is a tiny forwarding Stream that
            // disposes the response on Close/Dispose.
            return new ResponseOwningStream(response, response.ResponseStream);
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