using OnToPilot.Ontology;
using OnToPilot.Application.Foundation;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Fixture for <see cref="ReleaseManager"/> tests. Owns:
/// <list type="bullet">
/// <item>a temp dir for the workspace RocksDB (mutable);</item>
/// <item>a temp dir for the release artifact shards;</item>
/// <item>a temp dir for the published serving stores (separate RocksDB).</item>
/// </list>
/// On dispose every StoreWrapper is disposed and every temp dir is wiped.
/// </summary>
public sealed class ReleaseManagerFixture : IDisposable
{
    public string WorkspacePath { get; }
    public string ArtifactPath { get; }
    public string ServingPath { get; }

    public StoreWrapper Store { get; }
    public ReleaseArtifactStore Artifacts { get; }

    public ReleaseManagerFixture()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-relmgr-" + Guid.NewGuid().ToString("N"));
        WorkspacePath = System.IO.Path.Combine(root, "workspace");
        ArtifactPath = System.IO.Path.Combine(root, "artifacts");
        ServingPath = System.IO.Path.Combine(root, "serving");

        // Oxigraph's Store constructor requires the path to exist; create it.
        Directory.CreateDirectory(WorkspacePath);

        Store = new StoreWrapper(WorkspacePath);
        Artifacts = new ReleaseArtifactStore(ArtifactPath);
    }

    public void Dispose()
    {
        Store.Dispose();
        var root = System.IO.Path.GetDirectoryName(WorkspacePath)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class ReleaseManagerTests : IClassFixture<ReleaseManagerFixture>, IAsyncLifetime
{
    private readonly ReleaseManagerFixture _fx;
    private readonly KsContext _ks;
    private static readonly Actor ActorInstance = new("user-1", "User One");

    public ReleaseManagerTests(ReleaseManagerFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://ontopilot.local/ks/test/releasemgr",
            BaseIri: "http://ontopilot.local/ks/test/releasemgr/onto#");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        // The artifact store is a directory; wipe it between tests so each
        // test starts at v1. We don't delete the root, just its contents.
        foreach (var d in Directory.GetDirectories(_fx.ArtifactPath))
        {
            Directory.Delete(d, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static OntoQuad MakeQuad(string s, string p, string o, string g) =>
        new(new OntoNamedNode(s), new OntoNamedNode(p),
            new OntoLiteral(o), new OntoNamedNode(g));

    // ------------------------------------------------------------------
    // Required test (verbatim from the brief).
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Published_release_isolated_from_later_workspace_changes()
    {
        // Set up an initial workspace TBox.
        var initialQuad = MakeQuad("urn:s1", "urn:p", "v1", _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), [initialQuad]);

        // Use a per-fixture manager so the lifecycle is explicit.
        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);

        var laterQuad = MakeQuad("urn:s2", "urn:p", "v2", _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), [laterQuad]);

        Assert.DoesNotContain(laterQuad, releases.ReadPublished(release.Id, RdfLayer.TBox));
    }

    // ------------------------------------------------------------------
    // Captured release has the expected version + artifact path.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task CaptureAsync_assigns_v1_then_v2_then_reuses_v1_after_delete()
    {
        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);

        var v1 = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        Assert.Equal("v1", v1.Version);

        var v2 = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        Assert.Equal("v2", v2.Version);

        // Delete v1 → version slot must be freed.
        await releases.DeleteAsync(v1.Id, ActorInstance, CancellationToken.None);

        var v1Redux = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        Assert.Equal("v1", v1Redux.Version);
    }

    // ------------------------------------------------------------------
    // Published release is queryable for all three layers.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task ReadPublished_returns_quads_from_all_three_layers()
    {
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:t-s", "urn:p", "t-v", _ks.TBoxGraph)]);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.ABoxGraph),
            [MakeQuad("urn:a-s", "urn:p", "a-v", _ks.ABoxGraph)]);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.VocabularyGraph),
            [MakeQuad("urn:v-s", "urn:p", "v-v", _ks.VocabularyGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);

        Assert.Contains(releases.ReadPublished(release.Id, RdfLayer.TBox),
            q => q.Subject is OntoNamedNode n && n.Value == "urn:t-s");
        Assert.Contains(releases.ReadPublished(release.Id, RdfLayer.ABox),
            q => q.Subject is OntoNamedNode n && n.Value == "urn:a-s");
        Assert.Contains(releases.ReadPublished(release.Id, RdfLayer.Vocabulary),
            q => q.Subject is OntoNamedNode n && n.Value == "urn:v-s");
    }

    // ------------------------------------------------------------------
    // Concurrent workspace writes do not leak into the published view.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Concurrent_workspace_writes_do_not_leak_into_published_view()
    {
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s1", "urn:p", "v1", _ks.TBoxGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);

        // Race: many concurrent workspace writes — none should leak.
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
                [MakeQuad($"urn:race-{i}", "urn:p", $"v{i}", _ks.TBoxGraph)]);
        })).ToArray();
        await Task.WhenAll(tasks);

        // Serving view should only contain the original quad.
        var serving = releases.ReadPublished(release.Id, RdfLayer.TBox);
        Assert.Single(serving);
        Assert.Equal("urn:s1", ((OntoNamedNode)serving[0].Subject).Value);
    }

    // ------------------------------------------------------------------
    // DeleteAsync closes the serving store so a subsequent ReadPublished
    // throws.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task DeleteAsync_closes_serving_store()
    {
        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);
        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);
        Assert.True(releases.IsPublished(release.Id));

        await releases.DeleteAsync(release.Id, ActorInstance, CancellationToken.None);
        Assert.False(releases.IsPublished(release.Id));
        Assert.Throws<InvalidOperationException>(() => releases.ReadPublished(release.Id, RdfLayer.TBox));
    }

    // ------------------------------------------------------------------
    // PublishAsync on a non-existent release throws.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task PublishAsync_throws_for_missing_release()
    {
        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await releases.PublishAsync("no-such-release", ActorInstance, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // PublishAsync is idempotent — calling it twice does not double-load.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task PublishAsync_is_idempotent()
    {
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s1", "urn:p", "v1", _ks.TBoxGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);

        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);
        await releases.PublishAsync(release.Id, ActorInstance, CancellationToken.None);

        // Still only one quad in the published view.
        Assert.Single(releases.ReadPublished(release.Id, RdfLayer.TBox));
    }

    // ------------------------------------------------------------------
    // Capture writes shards for all three layers, even if one is empty.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Capture_writes_shards_for_all_three_layers_even_when_empty()
    {
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s", "urn:p", "v", _ks.TBoxGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);
        var release = await releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None);

        var manifest = _fx.Artifacts.LoadManifest(release.Id);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Contains(manifest.Files, f => f.Layer == "tbox" && f.StatementCount == 1);
        Assert.Contains(manifest.Files, f => f.Layer == "abox" && f.StatementCount == 0);
        Assert.Contains(manifest.Files, f => f.Layer == "vocabulary" && f.StatementCount == 0);
    }

    // ------------------------------------------------------------------
    // ConflictDetector signatures can be used to deduplicate two releases
    // that captured the same workspace.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Two_captures_of_same_workspace_yield_same_signature()
    {
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s", "urn:p", "v", _ks.TBoxGraph)]);

        var quads = _fx.Store.Match(graph: new OntoNamedNode(_ks.TBoxGraph));
        var sig1 = ConflictDetector.Signature(quads);

        // Mutate the workspace and back — same logical content → same sig.
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:tmp", "urn:p", "tmp", _ks.TBoxGraph)]);
        _fx.Store.RemoveQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:tmp", "urn:p", "tmp", _ks.TBoxGraph)]);

        var quadsAfter = _fx.Store.Match(graph: new OntoNamedNode(_ks.TBoxGraph));
        var sig2 = ConflictDetector.Signature(quadsAfter);

        Assert.Equal(sig1, sig2);
    }

    // ------------------------------------------------------------------
    // I-3 regression: concurrent CaptureAsync for the same KS must yield
    // distinct version strings. Without the manager lock around
    // AllocateVersion two captures can both observe the same existing
    // versions and both allocate "v1". With the lock, version allocation
    // is serialized so the N captures each get a unique version.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Concurrent_captures_yield_distinct_versions()
    {
        const int n = 8;
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s", "urn:p", "v", _ks.TBoxGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);

        var tasks = Enumerable.Range(0, n)
            .Select(_ => Task.Run(() => releases.CaptureAsync(_ks, ActorInstance, CancellationToken.None)))
            .ToArray();

        var captured = await Task.WhenAll(tasks);

        Assert.Equal(n, captured.Length);
        var versions = captured.Select(r => r.Version).ToList();
        Assert.Equal(n, versions.Distinct(StringComparer.Ordinal).Count());
        // Version numbers must be a contiguous range starting at v1
        // (no gaps when capturing into an empty artifact store).
        var numbers = versions.Select(v => int.Parse(v.AsSpan(1))).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(1, n).ToList(), numbers);
    }
}