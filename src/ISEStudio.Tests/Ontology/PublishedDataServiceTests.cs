using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Infrastructure;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Per-test fixture for <see cref="PublishedDataService"/>. Spins up a
/// real Oxigraph serving store, a real <see cref="ReleaseArtifactStore"/>
/// for the TBox shard, and an <see cref="SqliteContextFactory"/> for the
/// KS / release / deployment rows. Mirrors the <c>ExportServiceFixture</c>
/// "real collaborators" wiring so the resolver / Match / tbox-shard paths
/// actually traverse every layer.
/// </summary>
public sealed class PublishedDataServiceFixture : PostgresRdfFixture
{
    public const string GraphIri = "http://goodcrew.local/ks/published-tests";
    public const string BaseIri = GraphIri + "/onto#";

    public string Root { get; }
    public ReleaseArtifactStore Artifacts { get; }
    public ReleaseManager? Releases { get; private set; }
    public OntologyViewBuilder ViewBuilder { get; }
    private readonly string _root;

    public PublishedDataServiceFixture()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "isestudio-published-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        Root = _root;

        // ReleaseArtifactStore lays out releases under
        // {releasesRoot}/{releaseKey}/tbox.nq etc.
        var releasesRoot = Path.Combine(_root, "releases");
        Artifacts = new ReleaseArtifactStore(releasesRoot);
        Releases = null!;
        ViewBuilder = new OntologyViewBuilder();
    }

    /// <summary>
    /// Seed a fresh <see cref="KnowledgeSystemEntity"/>, write a
    /// minimal tbox shard, materialise the serving store with the
    /// supplied abox quads, insert <see cref="OntologyReleaseEntity"/>
    /// + <see cref="ReleaseDeploymentEntity"/> rows, and return the
    /// test handle so each test can target its own release id.
    /// </summary>
    public PublishedSeed SeedPublished(string version, IEnumerable<RdfStatement> aboxQuads)
    {
        // Clear prior release / deployment rows for this KS so the
        // partial unique index (KnowledgeSystemId, Version) where
        // Status<>'draft' doesn't reject a second published v1. We share
        // a single fixture (and KS) across tests in this class.
        Db.ChangeTracker.Clear();
        Db.ReleaseDeployments.RemoveRange(
            Db.ReleaseDeployments.Where(d => d.KnowledgeSystemId == KnowledgeSystemId));
        Db.OntologyReleases.RemoveRange(
            Db.OntologyReleases.Where(r => r.KnowledgeSystemId == KnowledgeSystemId));
        Db.SaveChanges();

        var ks = Db.KnowledgeSystems.Single(item => item.Id == KnowledgeSystemId);
        ks.PublicId = Guid.NewGuid().ToString("N");
        ks.Name = "Published data fixture";
        ks.Description = "";
        ks.GraphIri = GraphIri;
        ks.BaseIri = BaseIri;
        ks.UpdatedAt = DateTimeOffset.UtcNow;
        var releaseId = Guid.NewGuid();
        var releaseKey = releaseId.ToString("N");
        var ksContext = new KsContext(ks.GraphIri, ks.BaseIri,
            KnowledgeSystemId: KnowledgeSystemId);
        var deploymentId = Guid.NewGuid();

        // Persist KS update BEFORE the TBox/ABox AddStatements calls below.
        // Each PostgresRdfGraphStore.ReplaceLayerAsync implementation calls
        // ChangeTracker.Clear(), which would silently drop the KS
        // modification if we left it dangling in the tracker.
        Db.SaveChanges();

        TBox.AddStatements(ksContext.TBoxGraph,
            [new RdfStatement(
                new RdfIri(BaseIri + "Person"),
                ISEStudio.Ontology.Vocabulary.RdfType,
                new RdfIri(ISEStudio.Ontology.Vocabulary.OwlClass),
                ksContext.TBoxGraph)]);
        ABox.AddStatements(ksContext.ABoxGraph, aboxQuads.ToList());

        // 1) tbox.nq — one class declaration so GetClassesAsync has
        //    something to enumerate. Hand-roll the n-quads line (NQuadsTermWriter
        //    is internal to ISEStudio) — same shape the writer emits.
        var tboxNq =
            $"<{BaseIri}Person> <{ISEStudio.Ontology.Vocabulary.RdfType}> " +
            $"<{ISEStudio.Ontology.Vocabulary.OwlClass}> <{ksContext.TBoxGraph}> .\n";
        var tboxBytes = Encoding.UTF8.GetBytes(tboxNq);
        Artifacts.Write(releaseKey, RdfLayer.TBox, tboxBytes);
        Artifacts.SaveManifest(releaseKey, new ReleaseManifest(
            version,
            [Artifacts.BuildFileManifest(releaseKey, RdfLayer.TBox, tboxBytes)],
            1));

        // 2) Persist release and deployment rows.
        Db.OntologyReleases.Add(new OntologyReleaseEntity
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
        Db.ReleaseDeployments.Add(new ReleaseDeploymentEntity
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
        Db.SaveChanges();

        Releases = new ReleaseManager(Db, Statements, Artifacts);

        return new PublishedSeed(this, ks, releaseId, releaseKey, ksContext);
    }

    public PublishedDataService CreateService()
    {
        // Some tests (e.g. ResolveAsync_returns_null_for_unknown_*) never
        // call SeedPublished, so Releases is null until first use. Materialise
        // it lazily so the constructor doesn't NRE on the missing release
        // manager.
        Releases ??= new ReleaseManager(Db, Statements, Artifacts);
        return new PublishedDataService(Db, Releases, Artifacts, ViewBuilder, Statements);
    }

    public new async Task DisposeAsync()
    {
        Releases?.Dispose();
        await base.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* Directory handles can linger briefly. */ }
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

    private static RdfStatement MakeInstanceQuad(string iri, string classLocalName)
        => new(
            new RdfIri(BaseIriForInstance(iri)),
            Vocabulary.RdfType,
            new RdfIri(BaseIriForInstance(classLocalName)),
            PublishedDataServiceFixture.GraphIri + "/abox");

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
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
        using var svc = _fx.CreateService();
        var result = await svc.ResolveAsync(
            seed.Ks.PublicId, version: "v9", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Published")]
    public async Task ResolveAsync_returns_current_release_when_version_null()
    {
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
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
        var seed = _fx.SeedPublished("v3", Array.Empty<RdfStatement>());
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
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
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
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
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
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
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
        var seed = _fx.SeedPublished("v1", Array.Empty<RdfStatement>());
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