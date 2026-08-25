using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Oxigraph;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.External;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ExternalApiServiceTests
{
    // -- metadata --

    [Fact]
    public async Task GetMetadataAsync_unknown_public_id_returns_null()
    {
        await using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var meta = await svc.GetMetadataAsync("does-not-exist", MakeActor("u"), CancellationToken.None);
        Assert.Null(meta);
    }

    [Fact]
    public async Task GetMetadataAsync_returns_ks_stats()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-meta", classCount: 1, propCount: 2, axiomCount: 3);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var meta = await svc.GetMetadataAsync(ks.PublicId, MakeActor("u"), CancellationToken.None);

        Assert.NotNull(meta);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(meta));
        var root = doc.RootElement;
        Assert.Equal(ks.PublicId, root.GetProperty("id").GetString());
        Assert.Equal("ks-ext-meta", root.GetProperty("name").GetString());
        var stats = root.GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("classes").GetInt32());
        Assert.Equal(2, stats.GetProperty("properties").GetInt32());
        Assert.Equal(3, stats.GetProperty("axioms").GetInt32());
        // controlled_terms comes from the (empty) vocab graph → 0.
        Assert.Equal(0, stats.GetProperty("controlled_terms").GetInt32());
    }

    // -- classes --

    [Fact]
    public async Task ListClassesAsync_returns_class_with_count()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-classes");
        const string tbox =
            "@prefix ex: <http://example.com/ext-classes#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n" +
            "ex:Animal a owl:Class ; rdfs:label \"Animal\" .\n";
        await SeedTurtleAsync(app, ks, tbox, /*toABox*/ false);
        const string abox =
            "@prefix ex: <http://example.com/ext-classes#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .\n" +
            "ex:ind-1 a owl:NamedIndividual, ex:Animal .\n" +
            "ex:ind-2 a owl:NamedIndividual, ex:Animal .\n";
        await SeedTurtleAsync(app, ks, abox, /*toABox*/ true);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var out_ = await svc.ListClassesAsync(ks.PublicId, MakeActor("u"), CancellationToken.None);

        Assert.NotNull(out_);
        Assert.Equal(2, out_.Total);
        var cls = Assert.Single(out_.Classes);
        Assert.Equal("http://example.com/ext-classes#Animal", cls.Iri);
        Assert.Equal("Animal", cls.Label);
        Assert.Equal(2, cls.Count);
    }

    [Fact]
    public async Task ListClassesAsync_unknown_public_id_returns_null()
    {
        await using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var out_ = await svc.ListClassesAsync("nope", MakeActor("u"), CancellationToken.None);
        Assert.Null(out_);
    }

    // -- individuals --

    [Fact]
    public async Task ListIndividualsAsync_returns_paged_items()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-individuals");
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/ext-individuals#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .\n" +
            "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n" +
            "ex:ind-1 a owl:NamedIndividual ; rdfs:label \"Rex\" .\n" +
            "ex:ind-2 a owl:NamedIndividual ; rdfs:label \"Fido\" .\n",
            /*toABox*/ true);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var out_ = await svc.ListIndividualsAsync(
            ks.PublicId, null, null, 10, 0, MakeActor("u"), CancellationToken.None);

        Assert.NotNull(out_);
        Assert.Equal(2, out_.Total);
        Assert.Equal(2, out_.Items.Count);
        var labels = out_.Items.Select(i => i.Label).ToHashSet();
        Assert.Contains("Rex", labels);
        Assert.Contains("Fido", labels);
    }

    [Fact]
    public async Task ListIndividualsAsync_unknown_public_id_returns_null()
    {
        await using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var out_ = await svc.ListIndividualsAsync("nope", null, null, 10, 0, MakeActor("u"), CancellationToken.None);
        Assert.Null(out_);
    }

    // -- individual --

    [Fact]
    public async Task GetIndividualAsync_returns_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-individual");
        const string tbox =
            "@prefix ex: <http://example.com/ext-individual#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n" +
            "ex:Animal a owl:Class ; rdfs:label \"Animal\" .\n" +
            "ex:hasFriend a owl:ObjectProperty ; rdfs:label \"has friend\" .\n" +
            "ex:age a owl:DatatypeProperty ; rdfs:label \"age\" .\n";
        await SeedTurtleAsync(app, ks, tbox, /*toABox*/ false);
        const string abox =
            "@prefix ex: <http://example.com/ext-individual#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .\n" +
            "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n" +
            "ex:ind-1 a owl:NamedIndividual, ex:Animal ; rdfs:label \"Rex\" ;\n" +
            "    ex:hasFriend ex:ind-2 ; ex:age \"5\"^^<http://www.w3.org/2001/XMLSchema#integer> .\n" +
            "ex:ind-2 a owl:NamedIndividual, ex:Animal .\n";
        await SeedTurtleAsync(app, ks, abox, /*toABox*/ true);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var ind = await svc.GetIndividualAsync(
            ks.PublicId, "http://example.com/ext-individual#ind-1",
            MakeActor("u"), CancellationToken.None);

        Assert.NotNull(ind);
        Assert.Equal("http://example.com/ext-individual#ind-1", ind.Iri);
        Assert.Equal("Rex", ind.Label);
        var type = Assert.Single(ind.Types);
        Assert.Equal("http://example.com/ext-individual#Animal", type.Iri);
        var obj = Assert.Single(ind.ObjectAssertions);
        Assert.Equal("http://example.com/ext-individual#hasFriend", obj.Prop);
        Assert.Equal("http://example.com/ext-individual#ind-2", obj.Target);
        var data = Assert.Single(ind.DataAssertions);
        Assert.Equal("http://example.com/ext-individual#age", data.Prop);
        Assert.Equal("5", data.Value);
    }

    [Fact]
    public async Task GetIndividualAsync_unknown_iri_returns_null()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-individual-unknown");
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var ind = await svc.GetIndividualAsync(
            ks.PublicId, "http://example.com/missing#ind", MakeActor("u"), CancellationToken.None);
        Assert.Null(ind);
    }

    // -- export --

    [Fact]
    public async Task ExportAsync_returns_turtle_string()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "ext-export");
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/ext-export#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:Animal a owl:Class .\n",
            /*toABox*/ false);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var rdf = await svc.ExportAsync(
            ks.PublicId, RdfFormat.Turtle, MakeActor("u"), CancellationToken.None);

        Assert.NotNull(rdf);
        // DumpTurtle declares only rdf/rdfs/xsd prefixes, so owl:Class
        // is serialised as its full IRI — assert the class IRI instead.
        Assert.Contains("ext-export#Animal", rdf);
    }

    [Fact]
    public async Task ExportAsync_unknown_public_id_returns_null()
    {
        await using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalApiService>();
        var rdf = await svc.ExportAsync("nope", RdfFormat.Turtle, MakeActor("u"), CancellationToken.None);
        Assert.Null(rdf);
    }

    // --- helpers ---

    private static Actor MakeActor(string userId) => new(userId);

    private static async Task<KnowledgeSystemEntity> SeedKsAsync(
        AuthTestWebApplicationFactory app, string tag,
        int classCount = 0, int propCount = 0, int axiomCount = 0)
    {
        var db = app.CreateDbContext();
        // Seed a minimal admin user so the KS owner FK resolves (same
        // pattern as the SPARQL executor tests — Guid.Empty trips SQLite
        // FK enforcement).
        var ownerId = Guid.NewGuid();
        if (!db.Users.Any(u => u.Username == "external-admin"))
        {
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"), Id = ownerId,
                Username = "external-admin", DisplayName = "External Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 4),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            ownerId = db.Users.Single(u => u.Username == "external-admin").Id;
        }
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"), Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag, OwnerId = ownerId,
            PublicId = $"pub-{tag}",
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
            ClassCount = classCount, PropertyCount = propCount, AxiomCount = axiomCount,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static async Task SeedTurtleAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks,
        string turtle, bool toABox)
    {
        // Use a scope to grab the live StoreWrapper so the test bytes land
        // in the same Oxigraph instance the service reads.
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<StoreWrapper>();
        var ctx = KsContext.FromEntity(ks);
        var graph = new OntoNamedNode(toABox ? ctx.ABoxGraph : ctx.TBoxGraph);
        store.LoadTurtle(Encoding.UTF8.GetBytes(turtle), graph);
    }
}
