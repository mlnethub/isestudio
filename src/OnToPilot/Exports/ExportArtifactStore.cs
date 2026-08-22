using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OnToPilot.Exports;

/// <summary>
/// On-disk layout for export job shards. One subdirectory per job under
/// <see cref="RootPath"/> containing:
/// <list type="bullet">
///   <item>One N-Quads shard per layer (<c>tbox-0000.nq</c>,
///   <c>vocabulary-0000.nq</c>, <c>abox-0000.nq</c>) — MVP simplification:
///   one shard per layer regardless of <c>shard_size</c>. The shard-size
///   setting is persisted on the row but not enforced on disk; a future
///   hardening pass fans each layer into <c>N&gt;1</c> shards when the
///   statement count crosses the configured size.</item>
///   <item>A <c>manifest.json</c> summarising the bundle + per-shard
///   descriptor so the frontend can render a download picker.</item>
/// </list>
/// <para>Layout: <c>{ExportRoot}/{publicId}/{jobLegacyId}/...</c>. The
/// <see cref="KnowledgeSystemEntity.PublicId"/> namespace avoids cross-KS
/// collisions on the same host; <see cref="ExportJobEntity.LegacyId"/>
/// disambiguates multiple jobs of the same KS.</para>
/// </summary>
public sealed class ExportArtifactStore
{
    /// <summary>
    /// Compact JSON for the manifest — keeps the on-disk file
    /// human-readable when an operator needs to inspect an export after
    /// a crash. Matches <see cref="Ontology.ReleaseArtifactStore"/>'s
    /// <c>WriteIndented</c> style.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _rootPath;

    public ExportArtifactStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        Directory.CreateDirectory(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath => _rootPath;

    /// <summary>Directory holding <paramref name="jobLegacyId"/>'s shards.</summary>
    public string JobPath(string publicId, long jobLegacyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicId);
        return Path.Combine(_rootPath, Sanitize(publicId), jobLegacyId.ToString());
    }

    /// <summary>
    /// Ensure <paramref name="jobLegacyId"/>'s output directory exists
    /// and is empty (delete any previous shards). Called once at the
    /// start of <see cref="ExportRunner.RunAsync"/> so a re-run of the
    /// same job never mixes stale + new content.
    /// </summary>
    public string PrepareOutputDir(string publicId, long jobLegacyId)
    {
        var path = JobPath(publicId, jobLegacyId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Write one N-Quads shard to disk and return its descriptor. MVP
    /// always writes index 0; future shard-size support increments the
    /// index per <c>shard_size</c>-bounded chunk.
    /// </summary>
    public ExportFileEntry WriteShard(
        string publicId, long jobLegacyId, string layer, int shardIndex, byte[] nQuads)
    {
        ArgumentException.ThrowIfNullOrEmpty(layer);
        ArgumentNullException.ThrowIfNull(nQuads);
        var dir = JobPath(publicId, jobLegacyId);
        // Mirror ReleaseArtifactStore.Write: ensure the per-job directory
        // exists so tests and one-off writers don't have to call
        // PrepareOutputDir first.
        Directory.CreateDirectory(dir);
        var name = $"{layer}-{shardIndex:D4}.nq";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, nQuads);
        return new ExportFileEntry(
            Name: name,
            Layer: layer,
            Statements: StatementCount(nQuads),
            Bytes: nQuads.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(nQuads)).ToLowerInvariant());
    }

    /// <summary>
    /// Write the bundle manifest summarising the export's contents. The
    /// descriptor list travels through the row's <c>Files</c> JSON
    /// column so the API surface can re-render it without re-reading the
    /// shard files.
    /// </summary>
    public ExportFileEntry WriteManifest(
        string publicId, long jobLegacyId, object manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dir = JobPath(publicId, jobLegacyId);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOpts);
        var name = "manifest.json";
        File.WriteAllBytes(Path.Combine(dir, name), json);
        return new ExportFileEntry(
            Name: name,
            Layer: "manifest",
            Statements: 0,
            Bytes: json.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant());
    }

    /// <summary>
    /// Read a previously-written shard back. Returns <c>null</c> for any
    /// unsafe path (absolute, contains parent traversal, or escapes the
    /// job directory) so the caller can surface a stable 404 instead of
    /// crashing on a <see cref="FileNotFoundException"/>.
    /// </summary>
    public byte[]? ReadFile(string publicId, long jobLegacyId, string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);

        // Path-traversal guard. Mirror the Python
        // `backend/app/api/releases.py:769` heuristic: reject absolute
        // paths and any filename that tries to escape via `..`. The third
        // guard (Path.GetFileName(filename) != filename) rejects trailing
        // slashes / NULs / multi-segment names that survived the first
        // two.
        if (Path.IsPathRooted(filename)
            || filename.Contains("..", StringComparison.Ordinal)
            || Path.GetFileName(filename) != filename)
        {
            return null;
        }

        var path = Path.Combine(JobPath(publicId, jobLegacyId), filename);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Statement count encoded in an N-Quads shard. Mirrors
    /// <see cref="Ontology.ReleaseArtifactStore.StatementCount"/>: every
    /// <c>.</c> followed by whitespace terminates a statement; the loop
    /// avoids materialising the byte array as a string.
    /// </summary>
    public static long StatementCount(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        long count = 0;
        for (int i = 0; i < nQuads.Length; i++)
        {
            byte b = nQuads[i];
            if (b == (byte)'.' && i + 1 < nQuads.Length)
            {
                byte next = nQuads[i + 1];
                if (next == (byte)'\n' || next == (byte)'\r'
                    || next == (byte)' ' || next == (byte)'\t')
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Restrict filesystem-facing segments to a safe alphabet. Without
    /// this a public id like <c>../../etc</c> would let a hostile KS name
    /// escape the export root.
    /// </summary>
    private static string Sanitize(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
}