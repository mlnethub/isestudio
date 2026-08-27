using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ISEStudio.Exports;

/// <summary>
/// On-disk layout for export job shards. One subdirectory per knowledge
/// system under <see cref="RootPath"/> containing:
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
/// <para>Layout: <c>{ExportRoot}/{publicId}/...</c>. The
/// <see cref="KnowledgeSystemEntity.PublicId"/> namespace avoids
/// cross-KS collisions on the same host. Phase 3 dropped the
/// per-job subdirectory so the path no longer depends on
/// <see cref="ExportJobEntity"/> identity.</para>
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

    /// <summary>Directory holding <paramref name="publicId"/>'s shards.</summary>
    public string JobPath(string publicId)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicId);
        return Path.Combine(_rootPath, Sanitize(publicId));
    }

    /// <summary>
    /// Ensure <paramref name="publicId"/>'s output directory exists and
    /// is empty (delete any previous shards). Called once at the start of
    /// <see cref="ExportRunner.RunAsync"/> so a re-run of the same job
    /// never mixes stale + new content.
    /// </summary>
    public string PrepareOutputDir(string publicId)
    {
        var path = JobPath(publicId);
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
        string publicId, string layer, int shardIndex, byte[] nQuads)
    {
        ArgumentException.ThrowIfNullOrEmpty(layer);
        ArgumentNullException.ThrowIfNull(nQuads);
        var dir = JobPath(publicId);
        // Mirror ReleaseArtifactStore.Write: ensure the per-KS directory
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
        string publicId, object manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dir = JobPath(publicId);
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
    /// KS directory) so the caller can surface a stable 404 instead of
    /// crashing on a <see cref="FileNotFoundException"/>.
    /// </summary>
    public byte[]? ReadFile(string publicId, string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);

        // Path-traversal guard. Mirror the Python
        // `backend/app/api/releases.py:769` heuristic: reject absolute
        // paths and any filename that tries to escape via `..`. The third
        // guard (Path.GetFileName(filename) != filename) rejects trailing
        // slashes / NULs / multi-segment names that survived the first
        // two.
        if (!IsSafeFileName(filename))
        {
            return null;
        }

        var path = Path.Combine(JobPath(publicId), filename);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Read a previously-written shard, falling back to the pre-Phase-3
    /// on-disk layout when the current flat path misses.
    ///
    /// <para>Before Phase 3 the shard path carried a per-job subdirectory
    /// — <c>{root}/{publicId}/{jobLegacyId}/{layer}-NNNN.nq</c> — and since
    /// Phase 2 every job row carried <c>legacy_id = 0</c>, so historic data
    /// sits under a single <c>0/</c> subdir. Phase 3 dropped the column and
    /// with it the subdirectory, which would have made those completed jobs
    /// silently 404 on download.</para>
    ///
    /// <para>The fallback is deliberately narrow: it triggers only when the
    /// flat read misses, and only when <em>exactly one</em> numeric-named
    /// subdirectory exists under <c>{root}/{publicId}/</c>. Zero or two-plus
    /// numeric subdirs are ambiguous, so the caller keeps its 404 —
    /// guessing between candidate job directories would risk serving one
    /// job's artefact under another job's id. No <c>legacy_id</c> column is
    /// consulted (it no longer exists); the disk is the only source.</para>
    ///
    /// <para>Writes are unaffected: new jobs continue to use the flat
    /// layout via <see cref="WriteShard"/> / <see cref="WriteManifest"/>.
    /// Keeping this method here (rather than inlining disk probing in
    /// <see cref="ExportService"/>) preserves the invariant that exactly one
    /// type owns on-disk layout knowledge.</para>
    /// </summary>
    public byte[]? ReadFileWithLegacyLayoutFallback(string publicId, string filename)
    {
        var primary = ReadFile(publicId, filename);
        if (primary is not null) return primary;

        // Re-apply the traversal guard: the fallback path joins the same
        // untrusted filename onto a deeper directory.
        if (!IsSafeFileName(filename)) return null;

        var ksDir = JobPath(publicId);
        if (!Directory.Exists(ksDir)) return null;

        string? candidate = null;
        foreach (var dir in Directory.EnumerateDirectories(ksDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !name.All(char.IsAsciiDigit)) continue;
            // Two or more numeric subdirs → ambiguous, stay on 404.
            if (candidate is not null) return null;
            candidate = dir;
        }
        if (candidate is null) return null;

        var path = Path.Combine(candidate, filename);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Path-traversal guard shared by every disk read. Rejects absolute
    /// paths, parent traversal, and multi-segment / trailing-separator
    /// names that survived the first two checks.
    /// </summary>
    private static bool IsSafeFileName(string filename) =>
        !Path.IsPathRooted(filename)
        && !filename.Contains("..", StringComparison.Ordinal)
        && Path.GetFileName(filename) == filename;

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