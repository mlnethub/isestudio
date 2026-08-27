using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Exports;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Exports;

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
    private const string GraphIri = "http://goodcrew.local/ks/export-tests";
    private const string BaseIri = GraphIri + "/onto#";

    public string Root { get; }
    public SqliteContextFactory Contexts { get; }
    public StoreWrapper Store { get; }
    public ExportArtifactStore Artifacts { get; }
    public ReleaseArtifactStore ReleaseArtifacts { get; }
    public ExportJobStore Jobs { get; }
    public ExportRunner Runner { get; }
    public KsContext KsCtx { get; }

    public ExportServiceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "isestudio-export-svc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        Store = new StoreWrapper(Path.Combine(Root, "store"));
        SeedAllLayers();

        Contexts = new SqliteContextFactory();

        Artifacts = new ExportArtifactStore(Path.Combine(Root, "exports"));
        ReleaseArtifacts = new ReleaseArtifactStore(Path.Combine(Root, "releases"));
        Jobs = new ExportJobStore(Contexts, TimeProvider.System);
        Runner = new ExportRunner(Jobs, Artifacts, Store, ReleaseArtifacts, TimeProvider.System);
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
        var ex = await Assert.ThrowsAsync<ISEStudio.Api.ValidationException>(() =>
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
        var ex = await Assert.ThrowsAsync<ISEStudio.Api.ValidationException>(() =>
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
        await Assert.ThrowsAsync<ISEStudio.Api.ValidationException>(() =>
            svc.CreateAsync(ks.Id,
                new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 6_000_000),
                TestActor(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_release_bound_export_404_for_unknown_release()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        // Release-bound exports are now implemented — validate that an
        // unknown release_id returns 404 (KeyNotFoundException) rather
        // than the old "not implemented" 400.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
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
    public async Task CreateAsync_release_bound_export_reads_from_release_shards()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();

        // Seed a published release with capture_status="ready" + actual
        // shard artifacts on disk (what ReleaseManager.CaptureAsync writes).
        var releaseId = Guid.NewGuid();
        var releaseKey = releaseId.ToString("N");
        using (var db = _fx.Contexts.CreateDbContext())
        {
            db.OntologyReleases.Add(new OntologyReleaseEntity
            {
                Id = releaseId,
                KnowledgeSystemId = ks.Id,
                Version = "v1",
                Status = "published",
                Title = "rel-bound",
                Notes = "",
                Manifest = System.Text.Json.JsonDocument.Parse(
                    """{"capture_status":"ready","version":"v1"}"""),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        // Write the release's layer shards (mirrors CaptureAsync output).
        _fx.ReleaseArtifacts.Write(releaseKey, RdfLayer.TBox,
            System.Text.Encoding.UTF8.GetBytes("<http://ex/A> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/2002/07/owl#Class> .\n"));
        _fx.ReleaseArtifacts.Write(releaseKey, RdfLayer.Vocabulary,
            System.Text.Encoding.UTF8.GetBytes("<http://ex/t1> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/2004/02/skos/core#Concept> .\n"));
        _fx.ReleaseArtifacts.Write(releaseKey, RdfLayer.ABox,
            System.Text.Encoding.UTF8.GetBytes("<http://ex/ind1> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://ex/A> .\n"));

        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.Bundle, ReleaseId: releaseId, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(releaseId, job!.ReleaseId);

        var finished = await _fx.Jobs.WaitAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", finished.Status);
        Assert.True(finished.TotalStatements >= 3,
            $"Expected ≥3 statements from the three release shards; got {finished.TotalStatements}.");
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
    public async Task GetAsync_resolves_by_guid()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var job = await svc.CreateAsync(ks.Id,
            new ExportRequest(Layer: ExportLayer.TBox, ReleaseId: null, ShardSize: 100_000),
            TestActor(), CancellationToken.None);
        Assert.NotNull(job);

        var byGuid = await svc.GetAsync(ks.Id, job.Id.ToString(),
            CancellationToken.None);
        Assert.NotNull(byGuid);
        Assert.Equal(job.Id, byGuid!.Id);

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
        var ex = await Assert.ThrowsAsync<ISEStudio.Api.ExportFilePayloadException>(() =>
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