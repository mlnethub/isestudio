using ISEStudio.Exports;

namespace ISEStudio.Tests.Exports;

/// <summary>
/// Fixture owning a temp <see cref="ExportArtifactStore"/>; cleaned up
/// on dispose.
/// </summary>
public sealed class ExportArtifactStoreFixture : IDisposable
{
    public string Path { get; }
    public ExportArtifactStore Store { get; }

    public ExportArtifactStoreFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-export-" + Guid.NewGuid().ToString("N"));
        Store = new ExportArtifactStore(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}

public class ExportArtifactStoreTests : IClassFixture<ExportArtifactStoreFixture>
{
    private readonly ExportArtifactStoreFixture _fx;

    public ExportArtifactStoreTests(ExportArtifactStoreFixture fx) { _fx = fx; }

    private static byte[] NQuads(params (string s, string p, string o, string g)[] rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (s, p, o, g) in rows)
        {
            sb.Append('<').Append(s).Append("> <").Append(p).Append("> <")
              .Append(o).Append("> <").Append(g).Append("> .\n");
        }
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    [Trait("Category", "Export")]
    public void Constructor_creates_root_directory()
    {
        Assert.True(Directory.Exists(_fx.Path));
        Assert.True(Directory.Exists(_fx.Store.RootPath));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void WriteShard_round_trips_bytes()
    {
        var publicId = "ks-1";
        var bytes = NQuads(("urn:s1", "urn:p", "urn:o1", "urn:g"));
        var entry = _fx.Store.WriteShard(publicId, ExportLayer.TBox, 0, bytes);

        Assert.Equal("tbox-0000.nq", entry.Name);
        Assert.Equal(ExportLayer.TBox, entry.Layer);
        Assert.Equal(bytes.Length, entry.Bytes);
        Assert.Equal(1L, entry.Statements);

        var read = _fx.Store.ReadFile(publicId, entry.Name);
        Assert.NotNull(read);
        Assert.Equal(bytes, read);
    }

    [Fact]
    [Trait("Category", "Export")]
    public void WriteShard_sha256_is_stable_64_hex_chars()
    {
        var bytes = NQuads(("urn:s", "urn:p", "urn:o", "urn:g"));
        var entry = _fx.Store.WriteShard("ks", ExportLayer.ABox, 0, bytes);
        Assert.Equal(64, entry.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);

        // Writing the same bytes again produces the same hash.
        var second = _fx.Store.WriteShard("ks", ExportLayer.ABox, 0, bytes);
        Assert.Equal(entry.Sha256, second.Sha256);
    }

    [Fact]
    [Trait("Category", "Export")]
    public void WriteShard_changes_sha_when_bytes_change()
    {
        var first = _fx.Store.WriteShard("ks", ExportLayer.TBox, 0,
            NQuads(("urn:s", "urn:p", "urn:o", "urn:g")));
        var second = _fx.Store.WriteShard("ks", ExportLayer.TBox, 0,
            NQuads(("urn:s", "urn:p", "urn:other", "urn:g")));
        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    [Trait("Category", "Export")]
    public void StatementCount_counts_triple_terminators()
    {
        var bytes = NQuads(
            ("urn:s1", "urn:p", "urn:o1", "urn:g"),
            ("urn:s2", "urn:p", "urn:o2", "urn:g"),
            ("urn:s3", "urn:p", "urn:o3", "urn:g"));
        Assert.Equal(3L, ExportArtifactStore.StatementCount(bytes));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void StatementCount_handles_empty_bytes()
    {
        Assert.Equal(0L, ExportArtifactStore.StatementCount(Array.Empty<byte>()));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void PrepareOutputDir_clears_stale_shards()
    {
        var publicId = "ks-clear";
        _fx.Store.WriteShard(publicId, ExportLayer.TBox, 0,
            NQuads(("urn:a", "urn:p", "urn:b", "urn:g")));

        var dir = _fx.Store.PrepareOutputDir(publicId);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void WriteManifest_creates_manifest_with_layer_manifest()
    {
        var entry = _fx.Store.WriteManifest("ks-mf", new { layer = "bundle" });
        Assert.Equal("manifest.json", entry.Name);
        Assert.Equal("manifest", entry.Layer);
        Assert.Equal(0L, entry.Statements);
        Assert.True(entry.Bytes > 0);

        var bytes = _fx.Store.ReadFile("ks-mf", "manifest.json");
        Assert.NotNull(bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes!);
        Assert.Contains("\"layer\"", text);
        Assert.Contains("\"bundle\"", text);
    }

    [Fact]
    [Trait("Category", "Export")]
    public void ReadFile_rejects_parent_traversal()
    {
        Assert.Null(_fx.Store.ReadFile("ks", "../etc/passwd"));
        Assert.Null(_fx.Store.ReadFile("ks", ".."));
        Assert.Null(_fx.Store.ReadFile("ks", "a/b"));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void ReadFile_rejects_absolute_paths()
    {
        // Drive letters on Windows are an absolute-root trigger; the test
        // factory is platform-agnostic so the rooted form must use a
        // separator Path.IsPathRooted recognises on every OS.
        var rooted = Path.IsPathRooted("a/b") ? "a/b" : "/etc/passwd";
        Assert.Null(_fx.Store.ReadFile("ks", rooted));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void ReadFile_returns_null_for_missing_file()
    {
        Assert.Null(_fx.Store.ReadFile("ks", "missing.nq"));
    }

    [Fact]
    [Trait("Category", "Export")]
    public void JobPath_sanitises_public_id()
    {
        // Public id with hostile chars must not escape the export root.
        var jobPath = _fx.Store.JobPath("../../etc");
        Assert.True(jobPath.StartsWith(_fx.Store.RootPath, StringComparison.Ordinal));
        Assert.DoesNotContain("..", jobPath);
    }
}
