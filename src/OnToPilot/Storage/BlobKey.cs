namespace OnToPilot.Storage;

/// <summary>
/// Helpers for the content-addressed object key layout used by every
/// <see cref="IBlobStore"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// The legacy 3-segment layout (<c>{aa}/{bb}/{full_sha}</c>) is preserved
/// verbatim across the migration so that pre-existing <c>Document.storage_path</c>
/// rows from the Python backend still resolve to the same blob after the
/// data cutover: the first four hex characters of the SHA-256 prefix the
/// object's full digest, mirroring the Python upload module's directory
/// fanout.
/// </para>
/// </remarks>
public static class BlobKey
{
    /// <summary>
    /// Build the legacy 3-segment storage path for a given SHA-256 digest.
    /// The argument must be the lowercase or uppercase 64-character hex form;
    /// callers should pass lowercase to keep on-disk paths canonical.
    /// </summary>
    /// <param name="sha256">A 64-character hex SHA-256 digest.</param>
    /// <returns>The layout <c>{sha[..2]}/{sha[2..4]}/{sha}</c>.</returns>
    public static string LegacyPathFor(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64)
        {
            throw new ArgumentException(
                $"SHA-256 digest must be 64 hex characters; got {sha256.Length}.",
                nameof(sha256));
        }
        return $"{sha256[..2]}/{sha256[2..4]}/{sha256}";
    }
}
