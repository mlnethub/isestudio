using ISEStudio.Application.Vocabulary;
using ISEStudio.Ontology;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using KsContext = ISEStudio.Ontology.KsContext;

namespace ISEStudio.Tests.Ontology;

public sealed class SkosManagerFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public SkosManagerFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-skos-" + Guid.NewGuid().ToString("N"));
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

public class SkosManagerTests : IClassFixture<SkosManagerFixture>, IAsyncLifetime
{
    private readonly SkosManagerFixture _fx;
    private readonly KsContext _ks;

    public SkosManagerTests(SkosManagerFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/skos-mgr",
            BaseIri: "http://goodcrew.local/ks/test/skos-mgr/onto#");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Required cross-graph test (Task 3 step 1).
    // ------------------------------------------------------------------
    [Fact]
    public void Tbox_abox_and_vocabulary_are_isolated_named_graphs()
    {
        // Use the SKOS manager to create a concept + the ABox manager
        // through ABoxManager.CreateIndividual. We don't need the ABox
        // manager's surface here — re-implement the call inline so this
        // file remains standalone.
        var skos = new SkosManager(_fx.Store);

        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));
        var conceptIri = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c", PrefLabel: "Pump", Language: "en"));

        // Concept must not appear in TBox or ABox graphs.
        Assert.Empty(_fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.TBoxGraph));
        Assert.Empty(_fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.ABoxGraph));

        // Scheme must not appear in TBox or ABox graphs.
        Assert.Empty(_fx.Store.Match(subjectIri: schemeIri, graphIri: _ks.TBoxGraph));
        Assert.Empty(_fx.Store.Match(subjectIri: schemeIri, graphIri: _ks.ABoxGraph));
    }

    // ------------------------------------------------------------------
    // Scheme + concept create round-trip.
    // ------------------------------------------------------------------
    [Fact]
    public void CreateScheme_then_CreateConcept_round_trip_in_vocabulary_graph()
    {
        var skos = new SkosManager(_fx.Store);

        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));
        Assert.False(string.IsNullOrWhiteSpace(schemeIri));

        var conceptIri = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c", PrefLabel: "Pump", Language: "en"));
        Assert.False(string.IsNullOrWhiteSpace(conceptIri));

        // skos:Concept + skos:inScheme + skos:prefLabel all live in the vocab graph
        var vocab = new OntoNamedNode(_ks.VocabularyGraph);
        var matches = _fx.Store.Match(subjectIri: conceptIri, graphIri: _ks.VocabularyGraph);
        Assert.NotEmpty(matches);

        // The concept must be typed skos:Concept
        Assert.Contains(matches,
            q => q.Predicate.Value == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type"
                 && q.Object is OntoNamedNode t
                 && t.Value == "http://www.w3.org/2004/02/skos/core#Concept");

        // The pref label must exist with the right language tag.
        Assert.Contains(matches,
            q => q.Predicate.Value == "http://www.w3.org/2004/02/skos/core#prefLabel"
                 && q.Object is OntoLiteral lit
                 && lit.Value == "Pump"
                 && lit.Language == "en");
    }

    // ------------------------------------------------------------------
    // list_concepts: filter combinations (mapping/origin/status/date).
    // Mirrors the Python list_concepts() signature.
    // ------------------------------------------------------------------
    [Fact]
    public void ListConcepts_filters_by_mapping_origin_status_date()
    {
        var skos = new SkosManager(_fx.Store);

        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-active-mapped",
            PrefLabel: "Pump", Language: "en",
            Origin: "manual", Status: "active",
            MappedEntityIri: "urn:Ontology:Pump"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-active-standalone",
            PrefLabel: "Valve", Language: "en",
            Origin: "extraction", Status: "active"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-deprecated",
            PrefLabel: "Legacy", Language: "en",
            Origin: "manual", Status: "deprecated"));

        // No filter → all three.
        Assert.Equal(3, skos.ListConcepts(_ks).Total);

        // Filter by status
        Assert.Equal(1, skos.ListConcepts(_ks, Status: "deprecated").Total);
        Assert.Equal(2, skos.ListConcepts(_ks, Status: "active").Total);

        // Filter by mapping
        Assert.Equal(1, skos.ListConcepts(_ks, Mapping: "mapped").Total);
        Assert.Equal(2, skos.ListConcepts(_ks, Mapping: "standalone").Total);

        // Filter by origin
        Assert.Equal(2, skos.ListConcepts(_ks, Origin: "manual").Total);
        Assert.Equal(1, skos.ListConcepts(_ks, Origin: "extraction").Total);
    }

    // ------------------------------------------------------------------
    // Date filter on ListConcepts (Task 3 step 4): StartDate / EndDate
    // bounds on the concept's ModifiedAt (falling back to CreatedAt).
    // The filter is on the date portion (`stamp[..10]`) of the ISO-8601
    // string so we anchor the bounds to the same day we just wrote.
    // ------------------------------------------------------------------
    [Fact]
    public void ListConcepts_filters_by_start_and_end_date()
    {
        var skos = new SkosManager(_fx.Store);

        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-today", PrefLabel: "Today", Language: "en"));
        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-soon", PrefLabel: "Soon", Language: "en"));

        // Anchored at today's date in the same ISO format the manager
        // writes — the date filter compares only `stamp[..10]`.
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var lastYear = DateTimeOffset.UtcNow.AddYears(-1).ToString("yyyy-MM-dd");

        // Both concepts are dated today.
        Assert.Equal(2, skos.ListConcepts(_ks).Total);
        Assert.Equal(2, skos.ListConcepts(_ks, StartDate: today).Total);
        Assert.Equal(2, skos.ListConcepts(_ks, EndDate: today).Total);
        Assert.Equal(2, skos.ListConcepts(_ks, StartDate: today, EndDate: today).Total);

        // No concepts can fall within a window that starts tomorrow.
        Assert.Equal(0, skos.ListConcepts(_ks, StartDate: tomorrow).Total);

        // No concepts can match a window that ends last year.
        Assert.Equal(0, skos.ListConcepts(_ks, EndDate: lastYear).Total);

        // Date filter composes with the other filters. Both concepts are
        // Origin=manual by default, so the date+origin intersection keeps
        // both; an Origin that no concept has still produces zero.
        Assert.Equal(2, skos.ListConcepts(_ks, Origin: "manual", StartDate: today).Total);
        Assert.Equal(0, skos.ListConcepts(_ks, Origin: "agent", StartDate: today).Total);
    }

    // ------------------------------------------------------------------
    // Resolve: pref/alt/hidden match with score.
    // ------------------------------------------------------------------
    [Fact]
    public void Resolve_matches_pref_and_alt_labels_with_scores()
    {
        var skos = new SkosManager(_fx.Store);
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:pump", PrefLabel: "Pump", AltLabels: new[] { new SkosLabel("Centrifugal Pump", "en") },
            Language: "en"));

        var exact = skos.Resolve(_ks, "Pump", Language: "en");
        Assert.Single(exact.Items);
        Assert.Equal("urn:pump", exact.Items[0].Concept.Iri);

        var partial = skos.Resolve(_ks, "centrifugal", Language: "en");
        Assert.Single(partial.Items);
        // Alt-label hit score (0.98) < pref-label exact (1.0), but we only
        // have one concept so this still yields one match.
        Assert.Equal("urn:pump", partial.Items[0].Concept.Iri);
    }

    // ------------------------------------------------------------------
    // Cycle rejection on broader relations.
    // ------------------------------------------------------------------
    [Fact]
    public void UpdateConcept_rejects_broader_cycle()
    {
        var skos = new SkosManager(_fx.Store);
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        var a = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:a", PrefLabel: "A", Language: "en"));
        var b = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:b", PrefLabel: "B", Language: "en", Broader: new[] { a }));

        // Now try to make A broader of B → cycle A → B → A.
        Assert.Throws<SkosValidationException>(() =>
            skos.UpdateConcept(_ks, a, new SkosConceptData(
                Iri: a, PrefLabel: "A", Language: "en", Broader: new[] { b })));
    }

    // ------------------------------------------------------------------
    // Self-relation rejected.
    // ------------------------------------------------------------------
    [Fact]
    public void UpdateConcept_rejects_self_relation()
    {
        var skos = new SkosManager(_fx.Store);
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        var a = skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:a", PrefLabel: "A", Language: "en"));

        Assert.Throws<SkosValidationException>(() =>
            skos.UpdateConcept(_ks, a, new SkosConceptData(
                Iri: a, PrefLabel: "A", Language: "en", Related: new[] { a })));
    }

    // ------------------------------------------------------------------
    // Duplicate label rejected.
    // ------------------------------------------------------------------
    [Fact]
    public void CreateConcept_rejects_duplicate_label_in_same_scheme()
    {
        var skos = new SkosManager(_fx.Store);
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:a", PrefLabel: "Pump", Language: "en"));

        Assert.Throws<SkosValidationException>(() =>
            skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
                Iri: "urn:b", PrefLabel: "Pump", Language: "en")));
    }

    // ------------------------------------------------------------------
    // Capture / revert: a failed concept write reverts.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Failed_concept_write_reverts_via_MarkError()
    {
        var skos = new SkosManager(_fx.Store);
        var schemeIri = skos.CreateScheme(_ks, new SkosSchemeData(
            Iri: "urn:scheme", Title: "Pumps", DefaultLanguage: "en"));

        skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
            Iri: "urn:c-keep", PrefLabel: "Keep", Language: "en"));

        byte[] snapshot = _fx.Store.DumpNQuads(new OntoNamedNode(_ks.VocabularyGraph));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var capture = await _fx.Store.CaptureAsync(_ks.VocabularyGraph, revertOnError: false);
            skos.CreateConcept(_ks, schemeIri, new SkosConceptData(
                Iri: "urn:c-tmp", PrefLabel: "Tmp", Language: "en"));
            capture.MarkError();
            throw new InvalidOperationException();
        });

        Assert.Equal(snapshot, _fx.Store.DumpNQuads(new OntoNamedNode(_ks.VocabularyGraph)));
    }
}