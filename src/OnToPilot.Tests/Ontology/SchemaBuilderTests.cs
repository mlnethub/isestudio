using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Round-trip tests for <see cref="SchemaBuilder.BuildMutation"/> and
/// <see cref="SchemaBuilder.BuildView"/>. Each test owns a fresh on-disk
/// Oxigraph store so cases do not leak quads.
/// </summary>
public sealed class SchemaBuilderFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public SchemaBuilderFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-schema-" + Guid.NewGuid().ToString("N"));
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

public class SchemaBuilderTests : IClassFixture<SchemaBuilderFixture>, IAsyncLifetime
{
    private readonly SchemaBuilderFixture _fx;
    private readonly OntoNamedNode _graph = new("urn:tbox");
    private readonly string _baseIri = "http://example.com/ontology#";

    public SchemaBuilderTests(SchemaBuilderFixture fx) { _fx = fx; }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string SubjectIri(OntoQuad q) => q.Subject switch
    {
        OntoNamedNode n => n.Value,
        OntoBlankNode b => b.Value,
        _ => q.Subject.ToString() ?? "",
    };

    // ------------------------------------------------------------------
    // BuildMutation
    // ------------------------------------------------------------------

    [Fact]
    public void BuildMutation_emits_class_type_and_label()
    {
        var mut = new OntologyMutation(
            Classes: [new ClassMutation(Label: "Country")],
            ObjectProperties: [],
            DataProperties: [],
            Axioms: []);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);

        var classNode = new OntoNamedNode(_baseIri + "Country");
        var typeQuad = quads.Single(q => q.Predicate.Value.EndsWith("type") && SubjectIri(q).Equals(classNode.Value));
        Assert.Equal("http://www.w3.org/2002/07/owl#Class", typeQuad.Object is OntoNamedNode n ? n.Value : ((OntoLiteral)typeQuad.Object).Value);

        var labelQuad = quads.Single(q =>
            q.Predicate.Value.EndsWith("label") && q.Subject.Equals(classNode));
        Assert.Equal("Country", ((OntoLiteral)labelQuad.Object).Value);
    }

    [Fact]
    public void BuildMutation_emits_subclass_axiom()
    {
        var mut = new OntologyMutation(
            Classes:
            [
                new ClassMutation(Label: "Wine Region"),
                new ClassMutation(Label: "Region"),
            ],
            ObjectProperties: [],
            DataProperties: [],
            Axioms: [new AxiomMutation(Type: "subclass", Sub: "Wine Region", Super: "Region")]);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);

        var sub = new OntoNamedNode(_baseIri + "WineRegion");
        var sup = new OntoNamedNode(_baseIri + "Region");
        var subQuad = quads.Single(q =>
            SubjectIri(q).Equals(sub.Value)
            && q.Predicate.Value.EndsWith("subClassOf")
            && q.Object is OntoNamedNode subObj && subObj.Value == sup.Value);
        Assert.NotNull(subQuad);
    }

    [Fact]
    public void BuildMutation_emits_data_property_with_datatype_range()
    {
        var mut = new OntologyMutation(
            Classes: [new ClassMutation(Label: "Measurement")],
            ObjectProperties: [],
            DataProperties:
            [
                new PropertyMutation(
                    Label: "value",
                    Kind: "data",
                    Domain: "Measurement",
                    Range: "decimal"),
            ],
            Axioms: []);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);

        var propNode = new OntoNamedNode(_baseIri + "value");
        var rangeQuad = quads.Single(q => SubjectIri(q) == propNode.Value && q.Predicate.Value.EndsWith("range"));
        Assert.Equal("http://www.w3.org/2001/XMLSchema#decimal",
            rangeQuad.Object is OntoNamedNode ro ? ro.Value : "");
    }

    [Fact]
    public void BuildMutation_writes_to_graph_via_capture()
    {
        var mut = new OntologyMutation(
            Classes: [new ClassMutation(Label: "Country", Comment: "A sovereign geographic entity.")],
            ObjectProperties: [],
            DataProperties: [],
            Axioms: []);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);
        Assert.NotEmpty(quads);

        // Apply to the store via the standard capture pattern.
        _fx.Store.AddQuads(_graph, quads);

        Assert.True(_fx.Store.Count(graph: _graph) > 0);
    }

    // ------------------------------------------------------------------
    // BuildView
    // ------------------------------------------------------------------

    [Fact]
    public void BuildView_round_trips_class_with_superclass()
    {
        var mut = new OntologyMutation(
            Classes:
            [
                new ClassMutation(Label: "Wine Region"),
                new ClassMutation(Label: "Region"),
            ],
            ObjectProperties: [],
            DataProperties: [],
            Axioms: [new AxiomMutation(Type: "subclass", Sub: "Wine Region", Super: "Region")]);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);
        _fx.Store.AddQuads(_graph, quads);

        var view = SchemaBuilder.BuildView(_graph.Value, _fx.Store);

        var wineRegion = view.Classes.SingleOrDefault(c => c.Label == "Wine Region");
        Assert.NotNull(wineRegion);
        Assert.Contains(_baseIri + "Region", wineRegion!.Superclasses);
    }

    [Fact]
    public void BuildView_round_trips_data_property_range_label()
    {
        var mut = new OntologyMutation(
            Classes: [new ClassMutation(Label: "Measurement")],
            ObjectProperties: [],
            DataProperties:
            [
                new PropertyMutation(
                    Label: "value",
                    Kind: "data",
                    Domain: "Measurement",
                    Range: "decimal"),
            ],
            Axioms: []);

        var quads = SchemaBuilder.BuildMutation(_baseIri, mut, _graph.Value);
        _fx.Store.AddQuads(_graph, quads);

        var view = SchemaBuilder.BuildView(_graph.Value, _fx.Store);

        var dataProp = view.DataProperties.Single();
        Assert.Equal("value", dataProp.Label);
        Assert.Equal("xsd:decimal", dataProp.RangeLabel);
        Assert.Equal("Measurement", dataProp.DomainLabel);
    }

    [Fact]
    public void BuildView_returns_empty_when_graph_is_empty()
    {
        var view = SchemaBuilder.BuildView(_graph.Value, _fx.Store);

        Assert.Empty(view.Classes);
        Assert.Empty(view.ObjectProperties);
        Assert.Empty(view.DataProperties);
    }
}