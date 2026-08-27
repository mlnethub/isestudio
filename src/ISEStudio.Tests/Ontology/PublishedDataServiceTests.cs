using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Per-test fixture for <see cref="PublishedDataService"/>. Spins up a
/// real Oxigraph serving store, a real <see cref="ReleaseArtifactStore"/>
/// for the TBox shard, and an <see cref="SqliteContextFactory"/> for the
/// KS / release / deployment rows. Mirrors the <c>ExportServiceFixture</c>
/// "real collaborators" wiring so the resolver / Match / tbox-shard paths
/// actually traverse every layer.
/// </summary>
public sealed class PublishedDataServiceFixture : IDisposable
{
    public const string GraphIri = "http://goodcrew.local/ks/published-tests";
    public const string BaseIri = GraphIri + "/onto#";

    public string Root { get; }
    public SqliteContextFactory Contexts { get; }
    public ReleaseArtifactStore Artifacts { get; }
    public ReleaseManager Releases { get; }
    public OntologyViewBuilder ViewBuilder { get; }
    public StoreWrapper Workspace { get; }

    public PublishedDataServiceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "isestudio-published-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        // ReleaseArtifactStore lays out releases under
        // {releasesRoot}/{releaseKey}/tbox.nq etc.
        var releasesRoot = Path.Combine(Root, "releases");
        Artifacts = new ReleaseArtifactStore(releasesRoot);

        // ReleaseManager needs a workspace StoreWrapper for its ctor +
        // a serving root to host the per-release read-only DB. The
        // published-data service never reads from the workspace, but the
        // manager expects the dependency.
        var servingRoot = Path.Combine(Root, "serving");
        Workspace = new StoreWrapper(Path.Combine(Root, "workspace"));
        Releases = new ReleaseManager(Workspace, Artifacts, servingRoot);

        Contexts = new SqliteContextFactory();
        ViewBuilder = new OntologyViewBuilder();
    }

    /// <summary>
    /// Seed a fresh <see cref="KnowledgeSystemEntity"/>, write a
    /// minimal tbox shard, materialise the serving store with the
    /// supplied abox quads, insert <see cref="OntologyReleaseEntity"/>
    /// + <see cref="ReleaseDeploymentEntity"/> rows, and return the
    /// test handle so each test can target its own release id.
    /// </summary>
    public PublishedSeed SeedPublished(string version, IEnumerable<Oxigraph.Quad> aboxQuads)
    {
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Published data fixture",
            Description = "",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var releaseId = Guid.NewGuid();
        var releaseKey = releaseId.ToString("N");
        var ksContext = new KsContext(ks.GraphIri, ks.BaseIri);
        var deploymentId = Guid.NewGuid();

        // 1) tbox.nq — one class declaration so GetClassesAsync has
        //    something to enumerate. Hand-roll the n-quads line (NQuadsTermWriter
        //    is internal to ISEStudio) — same shape the writer emits.
        var tboxNq =
            $"<{BaseIri}Person> <{Vocabulary.RdfType.Value}> " +
            $"<{Vocabulary.OwlClass.Value}> <{ksContext.TBoxGraph}> .\n";
        Artifacts.Write(releaseKey, RdfLayer.TBox, Encoding.UTF8.GetBytes(tboxNq));

        // 2) Materialise the serving store at the manager's expected
        //    location. Writable → add quads → dispose → re-open path.
        //    ReleaseManager.IsPublished lazy-opens via OpenReadOnly so
        //    a valid Oxigraph DB must exist on disk.
        var servingPath = Releases.ServingPath(releaseKey);
        Directory.CreateDirectory(servingPath);
        using (var writable = new StoreWrapper(servingPath))
        {
            writable.AddQuads(
                new OntoNamedNode(ksContext.ABoxGraph),
                aboxQuads.ToList());
        }

        // 3) Persist rows.
        using (var db = Contexts.CreateDbContext())
        {
            db.KnowledgeSystems.Add(ks);
            db.OntologyReleases.Add(new OntologyReleaseEntity
            {
                Id = releaseId,
                KnowledgeSystemId = ks.Id,
                Version = version,
                Status = "published",
                Title = "published tests",
                Notes = "",
                Manifest = JsonDocument.Parse(
                    """{"manifest_file":{"sha256":"deadbeef"},"capture_status":"ready"}"""),
                CreatedAt = DateTimeOffset.UtcNow,
                PublishedAt = DateTimeOffset.UtcNow,
            });
            db.ReleaseDeployments.Add(new ReleaseDeploymentEntity
            {
                Id = deploymentId,
                KnowledgeSystemId = ks.Id,
                ReleaseId = releaseId,
                Status = "active",
                TboxGraphIri = ksContext.TBoxGraph,
                VocabularyGraphIri = ksContext.VocabularyGraph,
                AboxGraphIri = ksContext.ABoxGraph,
                StatementCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        return new PublishedSeed(this, ks, releaseId, releaseKey, ksContext);
    }

    /// <summary>
    /// Open a fresh read-only <see cref="StoreWrapper"/> against the
    /// same serving directory the service will open. Returned handle
    /// is the caller's responsibility to dispose.
    /// </summary>
    public StoreWrapper OpenServing(string releaseKey) =>
        StoreWrapper.OpenReadOnly(Releases.ServingPath(releaseKey));

    public PublishedDataService CreateService() =>
        new(Contexts.CreateDbContext(), Releases, Artifacts, ViewBuilder);

    public void Dispose()
    {
        Workspace.Dispose();
        Contexts.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* Oxigraph handle can linger briefly. */ }
    }
}

/// <summary>
/// Bundle returned by <see cref="PublishedDataServiceFixture.SeedPublished"/>
/// so tests can target the seeded KS / release / graph IRIs without
/// re-querying.
/// </summary>
public sealed record PublishedSeed(
    PublishedDataServiceFixture Fx,
    KnowledgeSystemEntity Ks,
    Guid ReleaseId,
    string ReleaseKey,
    KsContext KsContext);

public sealed class PublishedDataServiceTests : IClassFixture<PublishedDataServiceFixture>
{
    private readonly PublishedDataServiceFixture _fx;

    public PublishedDataServiceTests(PublishedDataServiceFixture fx) { _fx = fx; }

    private static Oxigraph.Quad MakeInstanceQuad(string iri, string classLocalName)
        => new(
            new OntoNamedNode(BaseIriForInstance(iri)),
            new OntoNamedNode(Vocabulary.RdfType.Value),
            new OntoNamedNode(BaseIriForInstance(classLocalName)),
            new OntoNamedNode(PublishedDataServiceFixture.GraphIri + "/abox"));

    private static string BaseIriForInstance(string local) =>
        PublishedDataServiceFixture.BaseIri + local;

    // ----------------------------------------------------------------------
    // ResolveAsync
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task ResolveAsync_returns_null_for_unknown_knowledge_system()
    {
        using var svc = _fx.CreateService();
        var result = await svc.ResolveAsync(
            "no-such-ks", version: null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task ResolveAsync_returns_null_for_unknown_pinned_version()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var result = await svc.ResolveAsync(
            seed.Ks.PublicId, version: "v9", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task ResolveAsync_returns_current_release_when_version_null()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(
            seed.Ks.PublicId, version: null, CancellationToken.None);
        Assert.NotNull(ctx);
        Assert.Equal(seed.ReleaseId, ctx!.Release.Id);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task ResolveAsync_returns_pinned_release_when_version_provided()
    {
        var seed = _fx.SeedPublished("v3", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(
            seed.Ks.PublicId, version: "v3", CancellationToken.None);
        Assert.NotNull(ctx);
        Assert.Equal(seed.ReleaseId, ctx!.Release.Id);
    }

    // ----------------------------------------------------------------------
    // metadata
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetMetadataAsync_returns_python_wire_shape()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var body = await svc.GetMetadataAsync(ctx!, new[] { "ontology:read", "instances:read" },
            CancellationToken.None);

        Assert.NotNull(body);
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(seed.Ks.PublicId, root.GetProperty("id").GetString());
        Assert.Equal(seed.Ks.Name, root.GetProperty("name").GetString());
        Assert.Equal(seed.Ks.BaseIri, root.GetProperty("baseIri").GetString());

        var rel = root.GetProperty("release");
        Assert.Equal("v1", rel.GetProperty("version").GetString());
        Assert.Equal(seed.ReleaseId, rel.GetProperty("id").GetGuid());
        Assert.Equal("deadbeef", rel.GetProperty("manifestSha256").GetString());

        var stats = root.GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("statements").GetInt32());
        Assert.Equal(0, stats.GetProperty("controlledTerms").GetInt32());

        var scopes = root.GetProperty("scopes");
        Assert.Equal(2, scopes.GetArrayLength());
    }

    // ----------------------------------------------------------------------
    // manifest
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetManifestAsync_returns_raw_manifest_json()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var manifest = svc.GetManifest(ctx!);
        Assert.NotNull(manifest);
        var jsonElement = Assert.IsType<JsonElement>(manifest);
        Assert.Equal("ready", jsonElement.GetProperty("capture_status").GetString());
        Assert.Equal("deadbeef",
            jsonElement.GetProperty("manifest_file").GetProperty("sha256").GetString());
    }

    // ----------------------------------------------------------------------
    // classes
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetClassesAsync_returns_class_with_count_from_abox()
    {
        // Seed one class + two instances of that class.
        var aliceQuad = MakeInstanceQuad("alice", "Person");
        var bobQuad = MakeInstanceQuad("bob", "Person");
        var seed = _fx.SeedPublished("v1", new[] { aliceQuad, bobQuad });
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var body = await svc.GetClassesAsync(ctx!, CancellationToken.None);
        Assert.NotNull(body);
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        var classes = doc.RootElement.GetProperty("classes");
        Assert.Equal(1, classes.GetArrayLength());

        var person = classes[0];
        Assert.Equal(BaseIriForInstance("Person"), person.GetProperty("iri").GetString());
        Assert.Equal(2, person.GetProperty("count").GetInt32());

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    // ----------------------------------------------------------------------
    // export
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetExportAsync_returns_tbox_nquads_bytes()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var bytes = svc.GetExport(ctx!);
        Assert.NotEmpty(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Person", text);
        Assert.Contains("owl#Class", text);
    }

    // ----------------------------------------------------------------------
    // individual / individuals
    // ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetIndividualAsync_returns_envelope_for_known_subject()
    {
        var aliceQuad = MakeInstanceQuad("alice", "Person");
        var seed = _fx.SeedPublished("v1", new[] { aliceQuad });
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var ind = await svc.GetIndividualAsync(
            ctx!, BaseIriForInstance("alice"), CancellationToken.None);
        Assert.NotNull(ind);
        Assert.Equal(BaseIriForInstance("alice"), ind!.Iri);
        Assert.Single(ind.Types);
        Assert.Equal(BaseIriForInstance("Person"), ind.Types[0].Iri);
        Assert.Empty(ind.ObjectAssertions);
        Assert.Empty(ind.DataAssertions);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task GetIndividualAsync_returns_null_for_unknown_subject()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<Oxigraph.Quad>());
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var ind = await svc.GetIndividualAsync(
            ctx!, BaseIriForInstance("ghost"), CancellationToken.None);
        Assert.Null(ind);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task ListIndividualsAsync_returns_paginated_match_python_shape()
    {
        var aliceQuad = MakeInstanceQuad("alice", "Person");
        var bobQuad = MakeInstanceQuad("bob", "Person");
        var seed = _fx.SeedPublished("v1", new[] { aliceQuad, bobQuad });
        using var svc = _fx.CreateService();
        var ctx = await svc.ResolveAsync(seed.Ks.PublicId, "v1", CancellationToken.None);
        Assert.NotNull(ctx);

        var result = await svc.ListIndividualsAsync(
            ctx!, classIri: null, q: null, limit: 20, offset: 0,
            CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Total);
        Assert.Equal(2, result.Items.Count);

        // Class-iri filter narrows the result.
        var filtered = await svc.ListIndividualsAsync(
            ctx!, classIri: BaseIriForInstance("Person"), q: null,
            limit: 20, offset: 0, CancellationToken.None);
        Assert.Equal(2, filtered!.Total);

        // Limit cuts to one row, total stays at 2.
        var paged = await svc.ListIndividualsAsync(
            ctx!, classIri: null, q: null,
            limit: 1, offset: 0, CancellationToken.None);
        Assert.Equal(2, paged!.Total);
        Assert.Single(paged.Items);
    }
}