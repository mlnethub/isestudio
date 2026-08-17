using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Each test instance owns a fresh on-disk Oxigraph store in its own temp
/// directory; both are torn down on dispose. The store is reset (cleared) at
/// the start of every test so cases do not leak quads.
/// </summary>
public sealed class OntologyEditorFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public OntologyEditorFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-editor-" + Guid.NewGuid().ToString("N"));
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

public class OntologyEditorTests : IClassFixture<OntologyEditorFixture>, IAsyncLifetime
{
    private readonly OntologyEditorFixture _fx;
    private readonly OntoNamedNode _graph = new("urn:tbox");
    private readonly string _baseIri = "http://example.com/ontology#";

    public OntologyEditorTests(OntologyEditorFixture fx) { _fx = fx; }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Catches F1 (revertOnError=true reverted every successful edit).
    // ------------------------------------------------------------------
    [Fact]
    public async Task Add_class_persists_after_capture_disposes()
    {
        var editor = new OntologyEditor(_fx.Store);
        var op = new Dictionary<string, object?>
        {
            ["op"] = "add_class",
            ["label"] = "Country",
        };
        var iri = await editor.ApplyEditAsync(_graph.Value, _baseIri, op);

        Assert.Equal(_baseIri + "Country", iri);

        // After ApplyEditAsync returns, the capture has been disposed and the
        // write must be committed, not rolled back.
        var view = SchemaBuilder.BuildView(_graph.Value, _fx.Store);
        var cls = view.Classes.SingleOrDefault(c => c.Label == "Country");
        Assert.NotNull(cls);
        Assert.Equal(_baseIri + "Country", cls!.Iri);
    }

    // ------------------------------------------------------------------
    // Catches F2 (EnsureLabeledClass did not write rdf:type / rdfs:label).
    // ------------------------------------------------------------------
    [Fact]
    public async Task Add_axiom_on_empty_graph_makes_both_classes_visible_in_view()
    {
        var editor = new OntologyEditor(_fx.Store);
        var addWine = new Dictionary<string, object?>
        {
            ["op"] = "add_class",
            ["label"] = "Wine Region",
        };
        var addRegion = new Dictionary<string, object?>
        {
            ["op"] = "add_class",
            ["label"] = "Region",
        };
        var addSubclass = new Dictionary<string, object?>
        {
            ["op"] = "add_axiom",
            ["type"] = "subclass",
            ["sub"] = "Wine Region",
            ["super"] = "Region",
        };

        await editor.ApplyEditAsync(_graph.Value, _baseIri, addWine);
        await editor.ApplyEditAsync(_graph.Value, _baseIri, addRegion);
        await editor.ApplyEditAsync(_graph.Value, _baseIri, addSubclass);

        var view = SchemaBuilder.BuildView(_graph.Value, _fx.Store);
        var wine = view.Classes.SingleOrDefault(c => c.Label == "Wine Region");
        var region = view.Classes.SingleOrDefault(c => c.Label == "Region");
        Assert.NotNull(wine);
        Assert.NotNull(region);

        // The subclass axiom must surface in the view's axiom set.
        Assert.Contains(view.Axioms.SubClassOf,
            p => p.A == _baseIri + "WineRegion" && p.B == _baseIri + "Region");
    }

    // ------------------------------------------------------------------
    // Catches F3 (UpdateProperty "Property not found" was unreachable).
    // ------------------------------------------------------------------
    [Fact]
    public async Task Update_property_on_unknown_iri_throws()
    {
        var editor = new OntologyEditor(_fx.Store);
        var op = new Dictionary<string, object?>
        {
            ["op"] = "update_property",
            ["iri"] = _baseIri + "ghostProperty",
            ["label"] = "Renamed",
        };
        await Assert.ThrowsAsync<OntologyEditException>(async () =>
            await editor.ApplyEditAsync(_graph.Value, _baseIri, op));
    }

    // ------------------------------------------------------------------
    // Catches F1 on the failure path: a thrown op must leave the graph
    // byte-identical to pre-edit state.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Throwing_op_leaves_graph_byte_identical()
    {
        // Pre-populate the graph with one quad so we can detect any drift.
        var existing = new OntoQuad(
            new OntoNamedNode(_baseIri + "ExistingClass"),
            new OntoNamedNode("http://www.w3.org/2002/07/owl#Class"),
            new OntoNamedNode("http://www.w3.org/2002/07/owl#Class"),
            _graph);
        _fx.Store.AddQuads(_graph, new[] { existing });
        byte[] before = _fx.Store.DumpNQuads(_graph);

        var editor = new OntologyEditor(_fx.Store);
        // update_class against a non-existent IRI is guaranteed to throw.
        var op = new Dictionary<string, object?>
        {
            ["op"] = "update_class",
            ["iri"] = _baseIri + "MissingClass",
            ["label"] = "NewLabel",
        };
        await Assert.ThrowsAsync<OntologyEditException>(async () =>
            await editor.ApplyEditAsync(_graph.Value, _baseIri, op));

        byte[] after = _fx.Store.DumpNQuads(_graph);
        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------------
    // F8 regression: Chinese "该 X 实例" pattern must match — no \b.
    // The pre-fix code used \b该 which .NET cannot match between CJK
    // characters (no boundary fires) so the pattern silently never fires.
    // ------------------------------------------------------------------
    [Fact]
    public void Has_explicit_individual_declaration_matches_chinese_phrase()
    {
        const string source = "该 Lazor8030ProdEquipCategory 实例是一种生产设备类别。";
        Assert.True(RoleEvidence.HasExplicitIndividualDeclaration(
            source, "Lazor8030ProdEquipCategory"));
    }

    [Fact]
    public void Has_explicit_individual_declaration_matches_chinese_shi_yi_ge_pattern()
    {
        const string source = "Lazor8030ProdEquipCategory 是一个实例。";
        Assert.True(RoleEvidence.HasExplicitIndividualDeclaration(
            source, "Lazor8030ProdEquipCategory"));
    }

    // ------------------------------------------------------------------
    // F8 negative: a class label not followed by "实例" must not match.
    // ------------------------------------------------------------------
    [Fact]
    public void Has_explicit_individual_declaration_does_not_match_chinese_description()
    {
        const string source = "Lazor8030ProdEquipCategory 是一个类别，可以描述多个机器。";
        Assert.False(RoleEvidence.HasExplicitIndividualDeclaration(
            source, "Lazor8030ProdEquipCategory"));
    }
}