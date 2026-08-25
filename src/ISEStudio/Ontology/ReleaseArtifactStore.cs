using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ISEStudio.Ontology;

/// <summary>
/// Owns the on-disk layout for release artifacts. One subdirectory per
/// release under <see cref="RootPath"/> containing:
///
/// <list type="bullet">
/// <item><c>manifest.json</c> — the <see cref="ReleaseManifest"/> summary.</item>
/// <item><c>tbox.nq</c>, <c>abox.nq</c>, <c>vocabulary.nq</c> — N-Quads shards,
/// one per <see cref="RdfLayer"/>, named after the layer enum's lowercase
/// form. Shard hashes are stored in the manifest so a corrupt or partially-
/// flushed release is detectable by clients.</item>
/// </list>
///
/// The store is intentionally file-system-only (no RocksDB). The
/// <see cref="ReleaseManager"/> opens a separate RocksDB directory at
/// publication time and loads the shards into it for read-only serving —
/// keeping the shards independent of the serving engine means a future
/// engine swap only changes the loader, not the artifact format.
/// </summary>
public sealed class ReleaseArtifactStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _rootPath;

    public ReleaseArtifactStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        Directory.CreateDirectory(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath => _rootPath;

    /// <summary>Path of the subdirectory used for <paramref name="releaseId"/>.</summary>
    public string ReleasePath(string releaseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        return Path.Combine(_rootPath, releaseId);
    }

    /// <summary>Returns true once a release directory exists on disk.</summary>
    public bool Exists(string releaseId)
    {
        var dir = ReleasePath(releaseId);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json"));
    }

    /// <summary>One per layer: returns the on-disk file name for that shard.</summary>
    public static string FileName(RdfLayer layer) => layer switch
    {
        RdfLayer.TBox => "tbox.nq",
        RdfLayer.ABox => "abox.nq",
        RdfLayer.Vocabulary => "vocabulary.nq",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    private static string Lower(RdfLayer layer) => layer switch
    {
        RdfLayer.TBox => "tbox",
        RdfLayer.ABox => "abox",
        RdfLayer.Vocabulary => "vocabulary",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    /// <summary>
    /// Write a shard to disk, creating the release directory if needed.
    /// Overwrites an existing shard of the same layer (caller is responsible
    /// for version management).
    /// </summary>
    public void Write(string releaseId, RdfLayer layer, byte[] nQuads)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentNullException.ThrowIfNull(nQuads);
        var dir = ReleasePath(releaseId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, FileName(layer)), nQuads);
    }

    /// <summary>Read a shard back. Throws <see cref="FileNotFoundException"/> if absent.</summary>
    public byte[] Read(string releaseId, RdfLayer layer)
    {
        var path = Path.Combine(ReleasePath(releaseId), FileName(layer));
        return File.ReadAllBytes(path);
    }

    /// <summary>SHA-256 (hex, lowercase) of a shard's current contents.</summary>
    public string Sha256(string releaseId, RdfLayer layer)
    {
        var bytes = Read(releaseId, layer);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Statement count encoded in a shard (number of <c>. \n</c> terminators).</summary>
    public static long StatementCount(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        long count = 0;
        // Iterate UTF-8 bytes so we don't allocate a string. Statement
        // terminator is `.` followed by whitespace — `\n` is the conventional
        // separator but we tolerate `\r\n` too.
        for (int i = 0; i < nQuads.Length; i++)
        {
            byte b = nQuads[i];
            if (b == (byte)'.' && i + 1 < nQuads.Length)
            {
                byte next = nQuads[i + 1];
                if (next == (byte)'\n' || next == (byte)'\r' || next == (byte)' ' || next == (byte)'\t')
                    count++;
            }
        }
        return count;
    }

    /// <summary>Persist a manifest next to the shards.</summary>
    public void SaveManifest(string releaseId, ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dir = ReleasePath(releaseId);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json, Encoding.UTF8);
    }

    /// <summary>Load a manifest. Throws if the release does not exist.</summary>
    public ReleaseManifest LoadManifest(string releaseId)
    {
        var path = Path.Combine(ReleasePath(releaseId), "manifest.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest not found for release '{releaseId}'.", path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Manifest for '{releaseId}' is empty.");
    }

    /// <summary>Delete the entire release subdirectory. No-op if absent.</summary>
    public void Delete(string releaseId)
    {
        var dir = ReleasePath(releaseId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// List known release ids (subdirectory names). Order is OS-dependent;
    /// callers needing sorted order should sort the result. The list is
    /// purely file-system-based; the in-memory serving registry lives with
    /// <see cref="ReleaseManager"/>.
    /// </summary>
    public IReadOnlyList<string> ListVersions()
    {
        if (!Directory.Exists(_rootPath)) return Array.Empty<string>();
        var dirs = Directory.GetDirectories(_rootPath);
        var list = new List<string>(dirs.Length);
        foreach (var d in dirs)
        {
            // Skip if no manifest — not a valid release.
            if (File.Exists(Path.Combine(d, "manifest.json")))
            {
                list.Add(Path.GetFileName(d));
            }
        }
        return list;
    }

    /// <summary>Per-layer summary used when writing the manifest.</summary>
    public ReleaseFileManifest BuildFileManifest(string releaseId, RdfLayer layer, byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        return new ReleaseFileManifest(
            Layer: Lower(layer),
            FileName: FileName(layer),
            StatementCount: StatementCount(nQuads),
            Sha256: Convert.ToHexString(SHA256.HashData(nQuads)).ToLowerInvariant());
    }
}