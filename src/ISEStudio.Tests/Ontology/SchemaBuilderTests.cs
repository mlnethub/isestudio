using ISEStudio.Ontology;
using ISEStudio.Tests.Infrastructure;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Round-trip tests for <see cref="SchemaBuilder.BuildMutationStatements"/> and
/// <see cref="SchemaBuilder.BuildView"/>. Each test owns a fresh store so
/// cases do not leak statements.
/// </summary>
public sealed class SchemaBuilderFixture : PostgresRdfFixture
{
}

public class SchemaBuilderTests : IClassFixture<SchemaBuilderFixture>, IAsyncLifetime
{
    private readonly SchemaBuilderFixture _fx;
    private readonly string _graph = "urn:tbox";
    private readonly string _baseIri = "http://example.com/ontology#";

    public SchemaBuilderTests(SchemaBuilderFixture fx) { _fx = fx; }

    public Task InitializeAsync() => _fx.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string SubjectIri(RdfStatement s) => s.Subject switch
    {
        RdfIri iri => iri.Value,
        RdfBlankNode blank => blank.Id,
        _ => s.Subject.ToString() ?? "",
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

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);

        var classIri = new RdfIri(_baseIri + "Country");
        var typeStatement = statements.Single(s =>
            s.PredicateIri.EndsWith("type") && SubjectIri(s).Equals(classIri.Value));
        Assert.Equal("http://www.w3.org/2002/07/owl#Class",
            typeStatement.Object is RdfIri iri ? iri.Value : ((RdfLiteral)typeStatement.Object).Value);

        var labelStatement = statements.Single(s =>
            s.PredicateIri.EndsWith("label") && s.Subject.Equals(classIri));
        Assert.Equal("Country", ((RdfLiteral)labelStatement.Object).Value);

        Assert.Contains(statements, s => s.Subject.Equals(classIri)
            && s.PredicateIri == Vocabulary.RdfType
            && s.Object is RdfIri rdfType && rdfType.Value == Vocabulary.OwlClass);
        Assert.Contains(statements, s => s.Subject.Equals(classIri)
            && s.PredicateIri == Vocabulary.RdfsLabel
            && s.Object is RdfLiteral lit && lit.Value == "Country");
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

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);

        var sub = new RdfIri(_baseIri + "WineRegion");
        var sup = new RdfIri(_baseIri + "Region");
        var subStatement = statements.Single(s =>
            SubjectIri(s).Equals(sub.Value)
            && s.PredicateIri.EndsWith("subClassOf")
            && s.Object is RdfIri subObj && subObj.Value == sup.Value);
        Assert.NotNull(subStatement);
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

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);

        var propNode = new RdfIri(_baseIri + "value");
        var rangeStatement = statements.Single(s => SubjectIri(s) == propNode.Value && s.PredicateIri.EndsWith("range"));
        Assert.Equal("http://www.w3.org/2001/XMLSchema#decimal",
            rangeStatement.Object is RdfIri ro ? ro.Value : "");
    }

    [Fact]
    public void BuildMutation_writes_to_graph_via_capture()
    {
        var mut = new OntologyMutation(
            Classes: [new ClassMutation(Label: "Country", Comment: "A sovereign geographic entity.")],
            ObjectProperties: [],
            DataProperties: [],
            Axioms: []);

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);
        Assert.NotEmpty(statements);

        // Apply to the store via the standard capture pattern.
        _fx.TBox.AddStatements(_graph, statements);

        Assert.NotEmpty(_fx.TBox.Match(graphIri: _graph));
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

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);
        _fx.TBox.AddStatements(_graph, statements);

        var view = SchemaBuilder.BuildView(
            _graph,
            _fx.Statements.ListAsync(_fx.KnowledgeSystemId, RdfLayer.TBox.ToString()).GetAwaiter().GetResult());

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

        var statements = SchemaBuilder.BuildMutationStatements(_baseIri, mut, _graph);
        _fx.TBox.AddStatements(_graph, statements);

        var view = SchemaBuilder.BuildView(
            _graph,
            _fx.Statements.ListAsync(_fx.KnowledgeSystemId, RdfLayer.TBox.ToString()).GetAwaiter().GetResult());

        var dataProp = view.DataProperties.Single();
        Assert.Equal("value", dataProp.Label);
        Assert.Equal("xsd:decimal", dataProp.RangeLabel);
        Assert.Equal("Measurement", dataProp.DomainLabel);
    }

    [Fact]
    public void BuildView_returns_empty_when_graph_is_empty()
    {
        var view = SchemaBuilder.BuildView(
            _graph,
            _fx.Statements.ListAsync(_fx.KnowledgeSystemId, RdfLayer.TBox.ToString()).GetAwaiter().GetResult());

        Assert.Empty(view.Classes);
        Assert.Empty(view.ObjectProperties);
        Assert.Empty(view.DataProperties);
    }
}