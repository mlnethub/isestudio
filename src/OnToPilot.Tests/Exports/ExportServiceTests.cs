using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Exports;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Exports;

/// <summary>
/// Fixture for the four <see cref="ExportService"/> ops. Owns a real
/// Oxigraph <see cref="StoreWrapper"/>, a temp
/// <see cref="ExportArtifactStore"/>, an <see cref="ExportJobStore"/>,
/// and a seeded <see cref="KnowledgeSystemEntity"/>. Mirrors the
/// <c>ExtractionStateTests</c> "real collaborators + seeded KS" wiring
/// so the runner actually traverses every layer.
/// </summary>
public sealed class ExportServiceFixture : IDisposable
{
    private const string GraphIri = "http://ontopilot.local/ks/export-tests";
    private const string BaseIri = GraphIri + "/onto#";

    public string Root { get; }
    public SqliteContextFactory Contexts { get; }
    public StoreWrapper Store { get; }
    public ExportArtifactStore Artifacts { get; }
    public ExportJobStore Jobs { get; }
    public ExportRunner Runner { get; }
    public KsContext KsCtx { get; }

    public ExportServiceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "ontopilot-export-svc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        Store = new StoreWrapper(Path.Combine(Root, "store"));
        SeedAllLayers();

        Contexts = new SqliteContextFactory();

        Artifacts = new ExportArtifactStore(Path.Combine(Root, "exports"));
        Jobs = new ExportJobStore(Contexts, TimeProvider.System);
        Runner = new ExportRunner(Jobs, Artifacts, Store, TimeProvider.System);
        KsCtx = new KsContext(GraphIri, BaseIri);
    }

    public void Dispose()
    {
        Store.Dispose();
        Contexts.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* Oxigraph handle can linger briefly. */ }
    }

    /// <summary>
    /// Seed a fresh <see cref="KnowledgeSystemEntity"/> and return it.
    /// Each test method calls this so its jobs land in an isolated KS —
    /// ListAsync ordering assertions stay deterministic.
    /// </summary>
    public KnowledgeSystemEntity SeedKnowledgeSystem()
    {
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Export service fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        using var db = Contexts.CreateDbContext();
        db.KnowledgeSystems.Add(ks);
        db.SaveChanges();
        return ks;
    }

    /// <summary>
    /// Build a scoped <see cref="ExportService"/> against this fixture's
    /// DbContext (mirrors the dispatcher arm's per-request scope).
    /// </summary>
    public ExportService CreateService() =>
        new(Contexts.CreateDbContext(), Jobs, Runner, Artifacts);

    private void SeedAllLayers()
    {
        // TBox: one rdf:type owl:Class triple.
        var tboxQuad = new Oxigraph.Quad(
            new OntoNamedNode(BaseIri + "Person"),
            new OntoNamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
            new OntoNamedNode("http://www.w3.org/2002/07/owl#Class"),
            new OntoNamedNode(GraphIri));
        Store.AddQuads(new OntoNamedNode(GraphIri), new[] { tboxQuad });

        // Vocabulary: one skos:Concept triple in the vocabulary graph.
        var vocabQuad = new Oxigraph.Quad(
            new OntoNamedNode(BaseIri + "vocab/Person"),
            new OntoNamedNode("http://www.w3.org/2004/02/skos/core#prefLabel"),
            new OntoLiteral("Person"),
            new OntoNamedNode(GraphIri + "/vocabulary"));
        Store.AddQuads(new OntoNamedNode(GraphIri + "/vocabulary"), new[] { vocabQuad });

        // ABox: one instance triple in the abox graph.
        var aboxQuad = new Oxigraph.Quad(
            new OntoNamedNode(BaseIri + "alice"),
            new OntoNamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
            new OntoNamedNode(BaseIri + "Person"),
            new OntoNamedNode(GraphIri + "/abox"));
        Store.AddQuads(new OntoNamedNode(GraphIri + "/abox"), new[] { aboxQuad });
    }
}

public class ExportServiceTests : IClassFixture<ExportServiceFixture>
{
    private readonly ExportServiceFixture _fx;

    public ExportServiceTests(ExportServiceFixture fx) { _fx = fx; }

    private static Actor TestActor() => new("test-user", "Tester");

    // ---------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_rejects_unsupported_layer()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var ex = await Assert.ThrowsAsync<OnToPilot.Api.ValidationException>(() =>
            svc.CreateAsync(ks.Id,
                new ExportRequest(Layer: "no-such-layer", ReleaseId: null, ShardSize: 100_000),
                TestActor(), CancellationToken.None));
        Assert.Contains("Unsupported export layer", ex.Message);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_rejects_shard_size_below_minimum()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var ex = await Assert.ThrowsAsync<OnToPilot.Api.ValidationException>(() =>
            svc.CreateAsync(ks.Id,
                new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 999),
                TestActor(), CancellationToken.None));
        Assert.Contains("Shard size", ex.Message);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_rejects_shard_size_above_maximum()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        await Assert.ThrowsAsync<OnToPilot.Api.ValidationException>(() =>
            svc.CreateAsync(ks.Id,
                new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 6_000_000),
                TestActor(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_refuses_release_bound_exports()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        // MVP: release-bound exports aren't implemented; the service
        // rejects the request rather than silently running the bundle.
        await Assert.ThrowsAsync<OnToPilot.Api.ValidationException>(() =>
            svc.CreateAsync(ks.Id,
                new ExportRequest(Layer: ExportLayer.Bundle, ReleaseId: Guid.NewGuid(), ShardSize: 100_000),
                TestActor(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_returns_null_for_unknown_knowledge_system()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var result = await svc.CreateAsync(Guid.NewGuid(),
            new ExportRequest(), TestActor(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task ListAsync_returns_null_for_unknown_knowledge_system()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var result = await svc.ListAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Happy paths
    // ---------------------------------------------------------------

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_bundle_writes_three_layers_and_completes()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id, new ExportRequest(),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(ExportLayer.Bundle, job!.Layer);

        // WaitAsync blocks until the runner flips status to a terminal.
        var finished = await _fx.Jobs.WaitAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", finished.Status);
        Assert.True(finished.TotalStatements >= 1,
            $"Expected at least one statement across the seeded layers; got {finished.TotalStatements}.");

        // The descriptor list must include one shard per layer + manifest.
        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.NotNull(fetched!.Files);
        var names = fetched.Files!.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tbox-0000.nq", names);
        Assert.Contains("vocabulary-0000.nq", names);
        Assert.Contains("abox-0000.nq", names);
        Assert.Contains("manifest.json", names);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_single_layer_writes_one_shard()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        var finished = await _fx.Jobs.WaitAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", finished.Status);

        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        var names = fetched!.Files!.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tbox-0000.nq", names);
        Assert.DoesNotContain("vocabulary-0000.nq", names);
        Assert.DoesNotContain("abox-0000.nq", names);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task GetAsync_resolves_by_guid_and_by_legacy_id()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);

        // The wire DTO (ExportOut) drops LegacyId — read it back from the
        // store so we can exercise the int→Guid fallback lookup path.
        var entity = await _fx.Jobs.GetAsync(job!.Id, CancellationToken.None);
        Assert.NotNull(entity);
        Assert.True(entity!.LegacyId > 0);

        var byGuid = await svc.GetAsync(ks.Id, job.Id.ToString(),
            CancellationToken.None);
        Assert.NotNull(byGuid);
        Assert.Equal(job.Id, byGuid!.Id);

        var byLegacy = await svc.GetAsync(ks.Id, entity.LegacyId.ToString(),
            CancellationToken.None);
        Assert.NotNull(byLegacy);
        Assert.Equal(job.Id, byLegacy!.Id);

        var missing = await svc.GetAsync(ks.Id, Guid.NewGuid().ToString(),
            CancellationToken.None);
        Assert.Null(missing);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task ListAsync_orders_newest_first()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var a = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        var b = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.ABox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);

        var result = await svc.ListAsync(ks.Id, CancellationToken.None);
        Assert.NotNull(result);
        var itemsProperty = result!.GetType().GetProperty("items");
        Assert.NotNull(itemsProperty);
        var items = (System.Collections.IEnumerable)itemsProperty!.GetValue(result)!;
        var ids = items.Cast<object>()
            .Select(o => o.GetType().GetProperty("Id")!.GetValue(o))
            .ToArray();
        Assert.Equal(new object[] { b!.Id, a!.Id }, ids);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_returns_payload_for_completed_job()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        await _fx.Jobs.WaitAsync(job!.Id, CancellationToken.None);

        // DownloadFileAsync throws ExportFilePayloadException — the
        // FastApiErrorMiddleware catches it. Assert the exception shape.
        var ex = await Assert.ThrowsAsync<OnToPilot.Api.ExportFilePayloadException>(() =>
            svc.DownloadFileAsync(ks.Id, job.Id.ToString(), "tbox-0000.nq",
                CancellationToken.None));
        Assert.Equal("application/n-quads", ex.MediaType);
        Assert.Equal("tbox-0000.nq", ex.FileName);
        Assert.NotEmpty(ex.Bytes);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_404s_when_filename_not_in_files_list()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        await _fx.Jobs.WaitAsync(job!.Id, CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.DownloadFileAsync(ks.Id, job.Id.ToString(), "evil.nq",
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_404s_on_parent_traversal()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        await _fx.Jobs.WaitAsync(job!.Id, CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.DownloadFileAsync(ks.Id, job.Id.ToString(), "../etc/passwd",
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_404s_when_status_not_completed()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        // Manually mint a pending row (without kicking the runner) so
        // download must reject on the status guard, not on missing file.
        var job = await _fx.Jobs.CreateAsync(
            ks.Id, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.DownloadFileAsync(ks.Id, job.Id.ToString(), "tbox-0000.nq",
                CancellationToken.None));
    }
}