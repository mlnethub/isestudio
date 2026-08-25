using ISEStudio.Ontology;
using Oxigraph;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Fixture for <see cref="ReleaseArtifactStore"/> tests. Owns a temp dir;
/// cleans up on dispose.
/// </summary>
public sealed class ReleaseStoreFixture : IDisposable
{
    public string Path { get; }
    public ReleaseArtifactStore Store { get; }

    public ReleaseStoreFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-release-" + Guid.NewGuid().ToString("N"));
        Store = new ReleaseArtifactStore(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public class ReleaseStoreTests : IClassFixture<ReleaseStoreFixture>
{
    private readonly ReleaseStoreFixture _fx;

    public ReleaseStoreTests(ReleaseStoreFixture fx) { _fx = fx; }

    private static byte[] NQuads(params (string s, string p, string o, string g)[] rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (s, p, o, g) in rows)
        {
            sb.Append('<').Append(s).Append("> <").Append(p).Append("> <").Append(o).Append("> <").Append(g).Append("> .\n");
        }
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Write_then_Read_round_trips_bytes()
    {
        var id = "rel-1";
        var bytes = NQuads(("urn:s", "urn:p", "urn:o", "urn:g"));
        _fx.Store.Write(id, RdfLayer.TBox, bytes);

        var read = _fx.Store.Read(id, RdfLayer.TBox);
        Assert.Equal(bytes, read);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Sha256_is_stable_and_changes_when_bytes_change()
    {
        var id = "rel-2";
        var bytes1 = NQuads(("urn:s", "urn:p", "urn:o", "urn:g"));
        _fx.Store.Write(id, RdfLayer.TBox, bytes1);
        var sha1 = _fx.Store.Sha256(id, RdfLayer.TBox);
        Assert.Equal(64, sha1.Length);

        var bytes2 = NQuads(("urn:s", "urn:p", "urn:other", "urn:g"));
        _fx.Store.Write(id, RdfLayer.TBox, bytes2);
        var sha2 = _fx.Store.Sha256(id, RdfLayer.TBox);
        Assert.NotEqual(sha1, sha2);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void StatementCount_counts_triple_terminators()
    {
        var bytes = NQuads(
            ("urn:s1", "urn:p", "urn:o1", "urn:g"),
            ("urn:s2", "urn:p", "urn:o2", "urn:g"),
            ("urn:s3", "urn:p", "urn:o3", "urn:g"));
        Assert.Equal(3L, ReleaseArtifactStore.StatementCount(bytes));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Save_then_Load_manifest_round_trips()
    {
        var id = "rel-3";
        var files = new List<ReleaseFileManifest>
        {
            new("tbox", "tbox.nq", 3, "deadbeef"),
            new("abox", "abox.nq", 5, "feedface"),
            new("vocabulary", "vocabulary.nq", 7, "cafebabe"),
        };
        var manifest = new ReleaseManifest("v1", files, 15);
        _fx.Store.SaveManifest(id, manifest);

        var loaded = _fx.Store.LoadManifest(id);
        Assert.Equal("v1", loaded.Version);
        Assert.Equal(15, loaded.ProvenanceCount);
        Assert.Equal(3, loaded.Files.Count);
        Assert.Equal("deadbeef", loaded.Files[0].Sha256);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Exists_returns_true_after_SaveManifest()
    {
        var id = "rel-4";
        Assert.False(_fx.Store.Exists(id));
        _fx.Store.SaveManifest(id, new ReleaseManifest("v1",
            new List<ReleaseFileManifest>(), 0));
        Assert.True(_fx.Store.Exists(id));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Delete_removes_directory_and_manifest()
    {
        var id = "rel-5";
        _fx.Store.Write(id, RdfLayer.TBox, NQuads(("urn:s", "urn:p", "urn:o", "urn:g")));
        _fx.Store.SaveManifest(id, new ReleaseManifest("v1",
            new List<ReleaseFileManifest>(), 1));
        Assert.True(_fx.Store.Exists(id));

        _fx.Store.Delete(id);
        Assert.False(_fx.Store.Exists(id));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void ListVersions_returns_only_releases_with_a_manifest()
    {
        _fx.Store.SaveManifest("rel-a", new ReleaseManifest("v1",
            new List<ReleaseFileManifest>(), 0));
        _fx.Store.SaveManifest("rel-b", new ReleaseManifest("v2",
            new List<ReleaseFileManifest>(), 0));

        // Directory without manifest.json should be ignored.
        Directory.CreateDirectory(System.IO.Path.Combine(_fx.Path, "rel-stray"));

        var list = _fx.Store.ListVersions();
        var set = new HashSet<string>(list, StringComparer.Ordinal);
        Assert.Contains("rel-a", set);
        Assert.Contains("rel-b", set);
        Assert.DoesNotContain("rel-stray", set);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void BuildFileManifest_records_layer_filename_count_and_hash()
    {
        var id = "rel-6";
        var bytes = NQuads(
            ("urn:s1", "urn:p", "urn:o1", "urn:g"),
            ("urn:s2", "urn:p", "urn:o2", "urn:g"));
        _fx.Store.Write(id, RdfLayer.TBox, bytes);

        var fm = _fx.Store.BuildFileManifest(id, RdfLayer.TBox, bytes);
        Assert.Equal("tbox", fm.Layer);
        Assert.Equal("tbox.nq", fm.FileName);
        Assert.Equal(2, fm.StatementCount);
        Assert.Equal(_fx.Store.Sha256(id, RdfLayer.TBox), fm.Sha256);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Delete_then_re_allocate_same_id_succeeds()
    {
        // Create v1, delete it, then create another release at the same id.
        var id = "rel-7";
        _fx.Store.Write(id, RdfLayer.TBox, NQuads(("urn:s", "urn:p", "urn:o", "urn:g")));
        _fx.Store.SaveManifest(id, new ReleaseManifest("v1",
            new List<ReleaseFileManifest>(), 1));
        _fx.Store.Delete(id);

        Assert.False(_fx.Store.Exists(id));

        // Recreate at the same id with different bytes — should succeed.
        var newBytes = NQuads(("urn:s", "urn:p", "urn:other", "urn:g"));
        _fx.Store.Write(id, RdfLayer.TBox, newBytes);
        _fx.Store.SaveManifest(id, new ReleaseManifest("v1-new",
            new List<ReleaseFileManifest>(), 1));
        Assert.True(_fx.Store.Exists(id));
        Assert.Equal(newBytes, _fx.Store.Read(id, RdfLayer.TBox));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Manifest_files_can_be_reordered_without_changing_semantic_content()
    {
        // Two manifests with identical content but different ordering of the
        // Files list should serialize to byte-distinct JSON (semantic diff
        // ignores order is a *consumer* property). For now assert that the
        // contents of the files are the same regardless of order — a
        // semantic diff that ignores file order lives at the manager level.
        var id = "rel-8";
        var fA = new ReleaseFileManifest("tbox", "tbox.nq", 1, "a");
        var fB = new ReleaseFileManifest("abox", "abox.nq", 1, "b");
        var fC = new ReleaseFileManifest("vocabulary", "vocabulary.nq", 1, "c");

        _fx.Store.SaveManifest(id, new ReleaseManifest("v1",
            new List<ReleaseFileManifest> { fA, fB, fC }, 3));
        var m1 = _fx.Store.LoadManifest(id);

        _fx.Store.SaveManifest(id, new ReleaseManifest("v1",
            new List<ReleaseFileManifest> { fC, fA, fB }, 3));
        var m2 = _fx.Store.LoadManifest(id);

        // Order differs in raw JSON; semantic content is identical when
        // sorted by Layer name.
        var layers1 = m1.Files.Select(f => f.Layer).OrderBy(x => x).ToList();
        var layers2 = m2.Files.Select(f => f.Layer).OrderBy(x => x).ToList();
        Assert.Equal(layers1, layers2);
        Assert.Equal(m1.ProvenanceCount, m2.ProvenanceCount);
    }
}