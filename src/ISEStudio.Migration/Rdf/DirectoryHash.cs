using System.Security.Cryptography;
using System.Text;

namespace ISEStudio.Migration.Rdf;

/// <summary>
/// Computes a deterministic SHA-256 fingerprint of every file under a
/// directory tree. The output is stable across runs and across operating
/// systems because we sort the file list lexicographically and use
/// forward-slash relative paths inside the hash input.
///
/// <para>This is the canary the data-cutover uses to prove the original
/// Python RocksDB source directory was never touched by the .NET
/// migration: take the fingerprint, run the migration, take the
/// fingerprint again, and assert byte-equality.</para>
///
/// <para>The hash input is a single UTF-8 string with the form
/// <c>{relative/path}:{hex-sha256-of-file-bytes}\n</c> for every file in
/// the tree, joined in sorted order. Empty directories contribute a
/// single line of <c>{relative/path}/(empty)\n</c> so that removing every
/// file from a directory also changes the hash.</para>
/// </summary>
public static class DirectoryHash
{
    /// <summary>Compute the SHA-256 fingerprint of <paramref name="root"/>.</summary>
    public static string Compute(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"DirectoryHash.Compute: '{root}' does not exist.");
        }

        var rootFull = Path.GetFullPath(root);
        var entries = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var relative = ToRelative(rootFull, filePath);
            var fileHash = Sha256OfFile(filePath);
            entries.Add($"{relative}:{fileHash}");
        }

        // Walk the directory structure too so adding/removing a file
        // flips the hash even if the file content happens to be the same
        // (defence-in-depth against a hypothetical malicious no-op write).
        foreach (var dirPath in Directory.EnumerateDirectories(rootFull, "*", SearchOption.AllDirectories))
        {
            var relative = ToRelative(rootFull, dirPath);
            entries.Add($"{relative}/(dir)");
        }

        var combined = string.Join("\n", entries);
        var bytes = Encoding.UTF8.GetBytes(combined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string Sha256OfFile(string path)
    {
        // Stream the file so very large RocksDB SST files don't get
        // buffered into a multi-GB byte[]. Oxigraph's RocksDB files can
        // run into hundreds of megabytes per shard.
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string ToRelative(string rootFull, string fileFull)
    {
        var relative = Path.GetRelativePath(rootFull, fileFull);
        // Normalise to forward slashes so the hash is identical on Windows
        // and Linux agents.
        return relative.Replace('\\', '/');
    }
}
