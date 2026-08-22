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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);
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
    public async Task AllocateVersion_assigns_v1_then_v2_then_reuses_v1_after_delete()
    {
        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);

        var v1 = releases.AllocateVersion();
        Assert.Equal("v1", v1);
        var rel1 = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), v1, ActorInstance, CancellationToken.None);

        var v2 = releases.AllocateVersion();
        Assert.Equal("v2", v2);
        await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), v2, ActorInstance, CancellationToken.None);

        // Delete rel1 → its v1 artifact slot is freed for reuse.
        await releases.DeleteAsync(rel1.Id, ActorInstance, CancellationToken.None);
        Assert.Equal("v1", releases.AllocateVersion());
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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);
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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);
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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);
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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);

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
        var release = await releases.CaptureAsync(_ks, Guid.NewGuid().ToString("N"), "v1", ActorInstance, CancellationToken.None);

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
    // I-3 regression: concurrent CaptureAsync for the same KS must NOT
    // corrupt each other's artifact shards. The 7a refactor moved
    // version allocation out of CaptureAsync (the service hands in an
    // explicit {releaseId, version}), so the manager no longer mints
    // versions — instead it serialises the per-release artifact writes
    // behind _versionLock so two concurrent captures cannot overlap on
    // the same release key. Each concurrent call here uses a distinct
    // release id and the same draft version; the lock around the
    // workspace CaptureAsync + Write should let all N finish and every
    // release key on disk should be readable.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Concurrent_captures_write_distinct_release_artifacts()
    {
        const int n = 8;
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph),
            [MakeQuad("urn:s", "urn:p", "v", _ks.TBoxGraph)]);

        using var releases = new ReleaseManager(_fx.Store, _fx.Artifacts, _fx.ServingPath);

        var releaseKeys = Enumerable.Range(0, n)
            .Select(_ => Guid.NewGuid().ToString("N"))
            .ToArray();

        var tasks = releaseKeys
            .Select(key => Task.Run(() => releases.CaptureAsync(
                _ks, key, "draft-v1", ActorInstance, CancellationToken.None)))
            .ToArray();

        var captured = await Task.WhenAll(tasks);

        Assert.Equal(n, captured.Length);
        // Every concurrent call returned its own Release record whose
        // releaseId / path matches the key we passed in — i.e., the
        // per-release artifacts are isolated by key, not by minting.
        Assert.Equal(releaseKeys.OrderBy(k => k, StringComparer.Ordinal),
            captured.Select(r => r.Id).OrderBy(k => k, StringComparer.Ordinal));
        // All versions are the same explicit "draft-v1" (the service
        // supplies the draft version; publish will mint v1 via
        // ReleaseService.NextVersionAsync).
        Assert.All(captured, r => Assert.Equal("draft-v1", r.Version));
        // Each artifact dir exists and is readable after the concurrent
        // burst — i.e., no truncation / partial-write from interleaved
        // writes.
        foreach (var r in captured)
        {
            var tbox = _fx.Artifacts.Read(r.Id, RdfLayer.TBox);
            Assert.NotEmpty(tbox);
        }
    }
}