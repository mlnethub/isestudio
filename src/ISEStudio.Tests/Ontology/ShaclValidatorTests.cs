using ISEStudio.Application.Vocabulary;
using ISEStudio.Ontology;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using KsContext = ISEStudio.Ontology.KsContext;

namespace ISEStudio.Tests.Ontology;

public sealed class ShaclValidatorFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public ShaclValidatorFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-shacl-" + Guid.NewGuid().ToString("N"));
        Store = new StoreWrapper(Path);
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public class ShaclValidatorTests : IClassFixture<ShaclValidatorFixture>, IAsyncLifetime
{
    private readonly ShaclValidatorFixture _fx;
    private readonly KsContext _ks;

    public ShaclValidatorTests(ShaclValidatorFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/shacl",
            BaseIri: "http://goodcrew.local/ks/test/shacl/onto#");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Mapping filter combinations (Task 3 step 4): mapped/standalone,
    // origin, status, date. This is the SKOSManager side; SHACL only kicks
    // in once the data graph violates a shape.
    // ------------------------------------------------------------------
    [Fact]
    public void Filter_combinations_match_python_list_concepts_contract()
    {
        var skos = new SkosManager(_fx.Store);

        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c1", PrefLabel: "Active Mapped", Language: "en",
            Origin: "manual", Status: "active", MappedEntityIri: "urn:Ontology:Pump"));
        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c2", PrefLabel: "Standalone", Language: "en",
            Origin: "extraction", Status: "active"));

        // combination: status=active AND mapping=mapped
        var r1 = skos.ListConcepts(_ks, Status: "active", Mapping: "mapped");
        Assert.Equal(1, r1.Total);
        Assert.Equal("urn:c1", r1.Items[0].Iri);

        // combination: status=active AND mapping=standalone
        var r2 = skos.ListConcepts(_ks, Status: "active", Mapping: "standalone");
        Assert.Equal(1, r2.Total);
        Assert.Equal("urn:c2", r2.Items[0].Iri);

        // combination: origin=extraction regardless of mapping
        var r3 = skos.ListConcepts(_ks, Origin: "extraction");
        Assert.Equal(1, r3.Total);
        Assert.Equal("urn:c2", r3.Items[0].Iri);

        // combination: origin=manual + status=active + mapping=mapped
        var r4 = skos.ListConcepts(_ks, Origin: "manual", Status: "active", Mapping: "mapped");
        Assert.Equal(1, r4.Total);
        Assert.Equal("urn:c1", r4.Items[0].Iri);

        // combination with no matches
        var r5 = skos.ListConcepts(_ks, Origin: "agent");
        Assert.Equal(0, r5.Total);
    }

    // ------------------------------------------------------------------
    // SHACL: the report must contain violations when the data graph
    // breaks a shape. Exercises focus-node collection, blank-node
    // property-shape lookup, and minCount evaluation end-to-end.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_reports_violations_when_data_breaks_a_shape()
    {
        // Build a minimal TBox + data graph in-memory. The shape graph is
        // loaded from the on-disk tbox-shapes.ttl file.
        var shapesPath = ResolveShapesPath();
        var shapeStore = new StoreWrapper(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-shacl-shapes-" + Guid.NewGuid().ToString("N")));

        try
        {
            // Load the SHACL shapes file as Turtle so its prefix
            // declarations are honoured. The shape file declares
            // op:OwlClassShape with sh:targetClass owl:Class and a
            // sh:property [ sh:path rdfs:label ; sh:minCount 1 ; sh:datatype xsd:string ].
            var shapesGraph = new OntoNamedNode("urn:shapes");
            shapeStore.LoadTurtle(System.IO.File.ReadAllBytes(shapesPath), shapesGraph);

            // Build a tiny data graph with one violation: a typed owl:Class
            // missing its required rdfs:label. The type must be owl:Class so
            // the focus-node collection actually finds it (focus-node
            // collection is by `sh:targetClass` value).
            var dataStore = _fx.Store;
            dataStore.AddQuads(new OntoNamedNode("urn:data"), new[]
            {
                new Oxigraph.Quad(
                    new OntoNamedNode("urn:i-missing-label"),
                    new OntoNamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
                    new OntoNamedNode("http://www.w3.org/2002/07/owl#Class"),
                    new OntoNamedNode("urn:data")),
            });

            var validator = new ShaclValidator(shapeStore, dataStore);
            var report = validator.Validate("urn:data");

            // The focus node has no rdfs:label → at least one violation.
            Assert.NotEmpty(report.Violations);
            // Every violation must point at the rdfs:label property shape.
            Assert.All(report.Violations, v =>
                Assert.Equal("http://www.w3.org/2000/01/rdf-schema#label", v.ResultPathIri));
            Assert.Contains(report.Violations, v => v.FocusNodeIri == "urn:i-missing-label");
            Assert.False(report.Conforms);
        }
        finally
        {
            shapeStore.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // The SHACL shape graph file must be present at the agreed path and
    // parse without error.
    // ------------------------------------------------------------------
    [Fact]
    public void Tbox_shapes_file_is_present_and_loadable()
    {
        var path = ResolveShapesPath();
        Assert.True(System.IO.File.Exists(path),
            $"SHACL shape file must exist at {path}");

        var bytes = System.IO.File.ReadAllBytes(path);
        Assert.NotEmpty(bytes);

        var probe = new StoreWrapper(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-shacl-probe-" + Guid.NewGuid().ToString("N")));
        try
        {
            // Should not throw. The shape file is Turtle, not NQuads.
            probe.LoadTurtle(bytes, new OntoNamedNode("urn:shapes"));
            Assert.True(probe.Count() > 0);
        }
        finally
        {
            probe.Dispose();
        }
    }

    private static string ResolveShapesPath()
    {
        // The shape file is shipped alongside the ISEStudio assembly; tests
        // resolve it relative to AppContext.BaseDirectory.
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "Ontology", "Shapes", "tbox-shapes.ttl");
    }
}