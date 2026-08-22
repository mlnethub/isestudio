using OnToPilot.Ontology;
using Oxigraph;
using OntoBlankNode = Oxigraph.BlankNode;
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

    // ------------------------------------------------------------------
    // I-1 regression (Stage 2): the sync-over-async bridge in DeleteClass
    // must surface GraphWriteConflictException (the 15s waitTimeout
    // contract from GraphWriteCoordinator) — not TimeoutException, and
    // not a deadlock. The fix routes the cascade through Task.Run so the
    // wait does not capture the calling thread's SynchronizationContext
    // or starve the pool; the cascade still opens its own capture on the
    // ABox graph and must obey that graph's per-key lock.
    //
    // The test holds a long-running write lease on the ABox graph, then
    // issues a delete_class op. The cascade inside DeleteClass blocks on
    // the held lease, hits the 15s waitTimeout, and throws
    // GraphWriteConflictException; ApplyEditAsync propagates it. Asserting
    // the exception type catches any future regression where the
    // sync-over-async bridge swallows the conflict as a bare
    // TimeoutException or deadlocks the test thread.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Delete_class_cascade_surfaces_GraphWriteConflictException_not_TimeoutException()
    {
        var editor = new OntologyEditor(_fx.Store);

        // 1) Seed a class so delete_class has something to remove.
        var clsIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "add_class",
                ["label"] = "Country",
            });

        // 2) Seed an instance typed against that class in the paired ABox
        //    graph so the cascade actually opens a capture on ABox. The
        //    cascade IRIs the ABox graph as "<tbox>/abox" — matches
        //    OntologyEditor.AboxIri which appends "/abox" to the trimmed
        //    TBox graph IRI.
        var aboxGraph = new OntoNamedNode(_graph.Value.TrimEnd('/') + "/abox");
        _fx.Store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(
                new OntoNamedNode("urn:instance-1"),
                new OntoNamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
                new OntoNamedNode(clsIri),
                aboxGraph),
        });

        // 3) Hold a write lease on the ABox graph for the duration of the
        //    test. The cascade's CaptureAsync(aboxGraph, ...) will block
        //    on the per-graph lock and, after the 15s waitTimeout,
        //    surface GraphWriteConflictException — proving the
        //    sync-over-async bridge preserves the conflict contract.
        await using var heldAbox = await _fx.Store.CaptureAsync(
            aboxGraph, revertOnError: false);

        // 4) Issue delete_class. Use a hard upper bound (30s) so a true
        //    deadlock still fails the test instead of hanging the run.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delOp = new Dictionary<string, object?>
        {
            ["op"] = "delete_class",
            ["iri"] = clsIri,
        };

        // 5) The exception type is the assertion. ApplyEditAsync's
        //    try/catch must rethrow the original exception after
        //    MarkError(), so we expect the conflict type to surface
        //    intact. A bare TimeoutException would indicate the
        //    sync-over-async bridge swallowed the conflict contract.
        var ex = await Assert.ThrowsAsync<GraphWriteConflictException>(async () =>
            await editor.ApplyEditAsync(_graph.Value, _baseIri, delOp, cts.Token));
        Assert.Contains("abox", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // set_property_union — builds an owl:unionOf blank-node expression
    // on a property's domain/range slot. Mirrors Python _set_property_union.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task SetPropertyUnion_creates_owl_unionOf_expression()
    {
        var editor = new OntologyEditor(_fx.Store);
        var wellIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Well" });
        var stationIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Station" });
        var propIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_property", ["label"] = "located" });

        var result = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "set_property_union",
                ["iri"] = propIri,
                ["slot"] = "range",
                ["members"] = new[] { wellIri, stationIri },
            });
        Assert.Equal(propIri, result);

        // The range slot must now point to a blank node.
        var rangeQuads = _fx.Store.Match(
            subjectIri: propIri,
            predicateIri: Vocabulary.RdfsRange.Value,
            graphIri: _graph.Value);
        Assert.Single(rangeQuads);
        Assert.IsType<OntoBlankNode>(rangeQuads[0].Object);

        var unionRoot = (OntoBlankNode)rangeQuads[0].Object;
        var unionTriples = _fx.Store.Match(graph: _graph)
            .Where(q => q.Subject is OntoBlankNode bn && bn.Value == unionRoot.Value)
            .ToList();
        // _:u a owl:Class + _:u owl:unionOf _:cell0
        Assert.Contains(unionTriples,
            q => q.Predicate.Value == Vocabulary.RdfType.Value
                && q.Object is OntoNamedNode o && o.Value == Vocabulary.OwlClass.Value);
        Assert.Contains(unionTriples,
            q => q.Predicate.Value == Vocabulary.OwlUnionOf.Value
                && q.Object is OntoBlankNode);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task SetPropertyUnion_replaces_old_union_and_GCs_blank_nodes()
    {
        var editor = new OntologyEditor(_fx.Store);
        var aIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Alpha" });
        var bIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Beta" });
        var cIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Gamma" });
        var propIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_property", ["label"] = "prop" });

        // First union: (Alpha, Beta)
        await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "set_property_union",
                ["iri"] = propIri,
                ["slot"] = "range",
                ["members"] = new[] { aIri, bIri },
            });
        var firstRange = _fx.Store.Match(
            subjectIri: propIri,
            predicateIri: Vocabulary.RdfsRange.Value,
            graphIri: _graph.Value);
        Assert.Single(firstRange);
        var oldRoot = (OntoBlankNode)firstRange[0].Object;

        // Replace with a new union: (Alpha, Gamma)
        await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "set_property_union",
                ["iri"] = propIri,
                ["slot"] = "range",
                ["members"] = new[] { aIri, cIri },
            });

        // The old union's blank-node subgraph must be gone (GC'd).
        var oldSurvivors = _fx.Store.Match(graph: _graph)
            .Where(q => q.Subject is OntoBlankNode bn && bn.Value == oldRoot.Value)
            .ToList();
        Assert.Empty(oldSurvivors);

        // Exactly one range slot remains, pointing to a fresh blank node.
        var newRange = _fx.Store.Match(
            subjectIri: propIri,
            predicateIri: Vocabulary.RdfsRange.Value,
            graphIri: _graph.Value);
        Assert.Single(newRange);
        Assert.IsType<OntoBlankNode>(newRange[0].Object);
    }

    // ------------------------------------------------------------------
    // merge_properties — repoint usage triples to target, union d/r,
    // drop sources, cascade ABox. Mirrors Python _merge_properties.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task MergeProperties_repoints_predicates_and_unions_slots()
    {
        var editor = new OntologyEditor(_fx.Store);
        var wellIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Well" });
        var stationIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Station" });

        // Two specialized properties, each with domain+range.
        var ownsWellIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "add_property", ["label"] = "owns Well",
                ["domain"] = "Well", ["range"] = "Well",
            });
        var ownsStationIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "add_property", ["label"] = "owns Station",
                ["domain"] = "Station", ["range"] = "Station",
            });

        // An ABox usage of ownsWell so the cascade path is exercised.
        var aboxGraph = new OntoNamedNode(_graph.Value.TrimEnd('/') + "/abox");
        _fx.Store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(
                new OntoNamedNode("urn:inst-1"),
                new OntoNamedNode(ownsWellIri),
                new OntoNamedNode("urn:target-1"),
                aboxGraph),
        });

        // Merge into a new "owns" property (no existing target → target_label).
        var result = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "merge_properties",
                ["sources"] = new[] { ownsWellIri, ownsStationIri },
                ["target_label"] = "owns",
            });
        Assert.Equal(_baseIri + "owns", result);

        // Sources must be gone (no definition triples remain).
        Assert.Empty(_fx.Store.Match(subjectIri: ownsWellIri, graphIri: _graph.Value));
        Assert.Empty(_fx.Store.Match(subjectIri: ownsStationIri, graphIri: _graph.Value));

        // Target is typed owl:ObjectProperty.
        Assert.True(_fx.Store.ContainsQuad(new OntoQuad(
            new OntoNamedNode(result),
            Vocabulary.RdfType,
            Vocabulary.OwlObjectProperty,
            _graph)));

        // The ABox assertion repointed to the target.
        var repointed = _fx.Store.Match(
            predicateIri: result, graphIri: aboxGraph.Value);
        Assert.Single(repointed);
        Assert.Empty(_fx.Store.Match(
            predicateIri: ownsWellIri, graphIri: aboxGraph.Value));
    }

    // ------------------------------------------------------------------
    // subordinate_properties — keep sources, add rdfs:subPropertyOf a
    // general target, union d/r onto target. Mirrors Python
    // _subordinate_properties.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task SubordinateProperties_adds_subPropertyOf_and_unions_target()
    {
        var editor = new OntologyEditor(_fx.Store);
        await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Well" });
        await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Station" });

        var ownsWellIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "add_property", ["label"] = "owns Well",
                ["domain"] = "Well", ["range"] = "Well",
            });
        var ownsStationIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "add_property", ["label"] = "owns Station",
                ["domain"] = "Station", ["range"] = "Station",
            });

        var target = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?>
            {
                ["op"] = "subordinate_properties",
                ["sources"] = new[] { ownsWellIri, ownsStationIri },
                ["target_label"] = "owns",
            });
        Assert.Equal(_baseIri + "owns", target);

        // Both sources survive (subordinate keeps them).
        Assert.NotEmpty(_fx.Store.Match(subjectIri: ownsWellIri, graphIri: _graph.Value));

        // Each source now has rdfs:subPropertyOf target.
        Assert.True(_fx.Store.ContainsQuad(new OntoQuad(
            new OntoNamedNode(ownsWellIri),
            Vocabulary.RdfsSubPropertyOf,
            new OntoNamedNode(target),
            _graph)));
        Assert.True(_fx.Store.ContainsQuad(new OntoQuad(
            new OntoNamedNode(ownsStationIri),
            Vocabulary.RdfsSubPropertyOf,
            new OntoNamedNode(target),
            _graph)));

        // Target's domain is a union (blank node) — both Well + Station.
        var domQuads = _fx.Store.Match(
            subjectIri: target,
            predicateIri: Vocabulary.RdfsDomain.Value,
            graphIri: _graph.Value);
        Assert.Single(domQuads);
        Assert.IsType<OntoBlankNode>(domQuads[0].Object);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task MergeProperties_target_in_sources_throws()
    {
        var editor = new OntologyEditor(_fx.Store);
        var aIri = await editor.ApplyEditAsync(_graph.Value, _baseIri,
            new Dictionary<string, object?> { ["op"] = "add_property", ["label"] = "a" });

        // target == one of the sources → must reject before any write.
        await Assert.ThrowsAsync<OntologyEditException>(async () =>
            await editor.ApplyEditAsync(_graph.Value, _baseIri,
                new Dictionary<string, object?>
                {
                    ["op"] = "merge_properties",
                    ["sources"] = new[] { aIri },
                    ["target"] = aIri,
                }));
    }
}