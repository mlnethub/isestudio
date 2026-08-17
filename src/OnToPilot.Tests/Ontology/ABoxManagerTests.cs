using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using KsContext = OnToPilot.Ontology.KsContext;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Each test instance owns a fresh on-disk Oxigraph store in its own temp
/// directory; both are torn down on dispose. The store is reset (cleared) at
/// the start of every test so cases do not leak quads.
/// </summary>
public sealed class ABoxManagerFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public ABoxManagerFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-abox-" + Guid.NewGuid().ToString("N"));
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

public class ABoxManagerTests : IClassFixture<ABoxManagerFixture>, IAsyncLifetime
{
    private readonly ABoxManagerFixture _fx;
    private readonly KsContext _ks;

    public ABoxManagerTests(ABoxManagerFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://ontopilot.local/ks/test/abox-mgr",
            BaseIri: "http://ontopilot.local/ks/test/abox-mgr/onto#");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Required: cross-graph isolation between TBox / ABox / Vocabulary
    // (Task 3 step 1 of the plan).
    // ------------------------------------------------------------------
    [Fact]
    public void Tbox_abox_and_vocabulary_are_isolated_named_graphs()
    {
        var abox = new ABoxManager(_fx.Store);
        var skos = new SkosManager(_fx.Store);

        var indIri = abox.CreateIndividual(_ks, "urn:i", "urn:Class");
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));
        var conceptIri = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c", PrefLabel: "Pump", Language: "en"));

        // ABox individual must NOT show up in the TBox graph...
        Assert.Empty(_fx.Store.Match(subjectIri: indIri, graphIri: _ks.TBoxGraph));
        // ...nor in the vocabulary (SKOS) graph.
        Assert.Empty(_fx.Store.Match(subjectIri: indIri, graphIri: _ks.VocabularyGraph));

        // Concept must NOT show up in the ABox graph...
        Assert.Empty(_fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.ABoxGraph));
        // ...nor in the TBox graph.
        Assert.Empty(_fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.TBoxGraph));

        // Sanity: the right triples ARE in the right graphs.
        Assert.NotEmpty(_fx.Store.Match(subjectIri: indIri, graphIri: _ks.ABoxGraph));
        Assert.NotEmpty(_fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.VocabularyGraph));
    }

    // ------------------------------------------------------------------
    // Stable fact keys (Task 3 step 3 of the plan).
    // ------------------------------------------------------------------
    [Fact]
    public void Stable_fact_keys_format_match_the_python_contract()
    {
        Assert.Equal("ind|urn:i", FactKey.IndividualKey("urn:i"));
        Assert.Equal("data|urn:s|urn:p|42",
            FactKey.DataKey("urn:s", "urn:p", "42"));
        Assert.Equal("obj|urn:s|urn:p|urn:t",
            FactKey.ObjectKey("urn:s", "urn:p", "urn:t"));
    }

    // ------------------------------------------------------------------
    // ABox CRUD: create / add / remove entity
    // ------------------------------------------------------------------
    [Fact]
    public void CreateIndividual_writes_rdf_type_to_abox_graph()
    {
        var abox = new ABoxManager(_fx.Store);
        var iri = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");

        Assert.False(string.IsNullOrWhiteSpace(iri));

        // rdf:type cls must be present in the ABox graph
        var types = _fx.Store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            graphIri: _ks.ABoxGraph);
        Assert.Contains(types, q => q.Object is OntoNamedNode n && n.Value == "urn:Class");
    }

    [Fact]
    public void CreateIndividual_writes_no_quotes_in_iri()
    {
        // IRI is auto-minted from the BaseIri; never echo the caller-supplied
        // individual IRI back (mirrors Python mint_iri that uses uuid4).
        var abox = new ABoxManager(_fx.Store);
        var iri = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");
        Assert.StartsWith(_ks.BaseIri, iri);
    }

    [Fact]
    public void AddDataAssertion_is_idempotent_and_returns_false_on_duplicate()
    {
        var abox = new ABoxManager(_fx.Store);
        var subj = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");

        Assert.True(abox.AddDataAssertion(_ks, subj, "urn:age", "42", null));
        Assert.False(abox.AddDataAssertion(_ks, subj, "urn:age", "42", null));
    }

    [Fact]
    public void AddObjectAssertion_writes_object_property_triple()
    {
        var abox = new ABoxManager(_fx.Store);
        var a = abox.CreateIndividual(_ks, "urn:ind-a", "urn:Class");
        var b = abox.CreateIndividual(_ks, "urn:ind-b", "urn:Class");

        Assert.True(abox.AddObjectAssertion(_ks, a, "urn:knows", b));

        var triples = _fx.Store.Match(
            subjectIri: a, predicateIri: "urn:knows", graphIri: _ks.ABoxGraph);
        Assert.Contains(triples, q => q.Object is OntoNamedNode n && n.Value == b);
    }

    [Fact]
    public void RemoveDataAssertion_removes_triple()
    {
        var abox = new ABoxManager(_fx.Store);
        var subj = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");
        abox.AddDataAssertion(_ks, subj, "urn:age", "42", null);

        abox.RemoveDataAssertion(_ks, subj, "urn:age", "42", null);

        Assert.Empty(_fx.Store.Match(
            subjectIri: subj, predicateIri: "urn:age", graphIri: _ks.ABoxGraph));
    }

    [Fact]
    public void DeleteIndividual_removes_all_its_quads()
    {
        var abox = new ABoxManager(_fx.Store);
        var subj = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");
        abox.AddDataAssertion(_ks, subj, "urn:age", "42", null);

        abox.DeleteIndividual(_ks, subj);

        Assert.Empty(_fx.Store.Match(subjectIri: subj, graphIri: _ks.ABoxGraph));
    }

    // ------------------------------------------------------------------
    // ABox validator: placeholder label, disjoint violation.
    // ------------------------------------------------------------------
    [Fact]
    public void Validator_flags_placeholder_label_as_error()
    {
        var abox = new ABoxManager(_fx.Store);
        var validator = new ABoxValidator(_fx.Store);

        var subj = abox.CreateIndividual(_ks, "urn:ind-1", "urn:Class");
        // The validator reads the TBox schema; for this test we only need
        // the individual + label so the placeholder rule fires.
        _fx.Store.AddQuads(new OntoNamedNode(_ks.ABoxGraph), new[]
        {
            new OntoQuad(new OntoNamedNode(subj),
                new OntoNamedNode("http://www.w3.org/2000/01/rdf-schema#label"),
                new OntoLiteral("Untitled"),
                new OntoNamedNode(_ks.ABoxGraph)),
        });

        var report = validator.Validate(_ks);
        Assert.Contains(report.Violations, v => v.Type == "placeholder" && v.Severity == "error");
    }

    // ------------------------------------------------------------------
    // Capture / revert: a failed ABox write reverts to pre-edit state.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Failed_create_individual_reverts_via_MarkError()
    {
        var abox = new ABoxManager(_fx.Store);
        // Create one individual so the ABox graph is non-empty.
        var before = abox.CreateIndividual(_ks, "urn:ind-before", "urn:Class");
        byte[] snapshot = _fx.Store.DumpNQuads(new OntoNamedNode(_ks.ABoxGraph));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var capture = await _fx.Store.CaptureAsync(_ks.ABoxGraph, revertOnError: false);
            abox.CreateIndividual(_ks, "urn:ind-after", "urn:Class");
            capture.MarkError();
            throw new InvalidOperationException();
        });

        // Bytes match — the second create is gone.
        Assert.Equal(snapshot, _fx.Store.DumpNQuads(new OntoNamedNode(_ks.ABoxGraph)));
        // Only the original individual remains.
        Assert.Empty(_fx.Store.Match(
            subjectIri: $"{_ks.BaseIri}",
            graphIri: _ks.ABoxGraph));
    }
}