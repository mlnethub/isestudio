using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Ontology;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using KsContext = ISEStudio.Ontology.KsContext;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Unit tests for the deterministic SKOS terminology sync's default-scheme
/// seeding (P1-1). The Python backend's <c>sync_from_ontology</c> calls
/// <c>ensure_scheme()</c> before creating concepts and writes
/// <c>skos:inScheme</c> on every fresh concept; the .NET port initially
/// omitted both, leaving a knowledge system with a populated concept list but
/// <c>scheme_count = 0</c> (which disables the "New term" button because
/// <c>selectedSchemeIri</c> is empty). These tests pin the parity fix.
/// </summary>
public sealed class TerminologyServiceFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public TerminologyServiceFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "isestudio-term-" + Guid.NewGuid().ToString("N"));
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

public class TerminologyServiceTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public TerminologyServiceTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-sync",
            BaseIri: "http://goodcrew.local/ks/test/term-sync/onto#",
            Name: "Pump systems");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_creates_default_scheme_when_vocabulary_is_empty()
    {
        SeedClasses("Pump", "Motor");

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(1, view.Stats.SchemeCount);
        Assert.Equal(2, view.Stats.ConceptCount);

        var scheme = Assert.Single(view.Schemes);
        Assert.Equal($"{_ks.VocabularyGraph}#scheme-extracted", scheme.Iri);
        Assert.Equal("extraction", scheme.Origin);
        Assert.Equal("Pump systems terminology", scheme.Title);

        // Every concept must anchor to the freshly-created default scheme.
        Assert.All(view.Concepts, c => Assert.Equal(scheme.Iri, c.SchemeIri));
    }

    [Fact]
    public void Sync_is_idempotent_and_reuses_existing_scheme()
    {
        SeedClasses("Pump");

        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(_ks, CancellationToken.None);
        var second = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(second.Error);
        // The concept is already mapped on the second pass, so nothing new.
        Assert.Equal(0, second.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(1, view.Stats.SchemeCount);
        Assert.Equal(1, view.Stats.ConceptCount);
    }

    [Fact]
    public void Sync_with_chinese_name_uses_chinese_scheme_title()
    {
        SeedClasses("Pump");
        var zh = _ks with { Name = "泵系统" };

        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(zh, CancellationToken.None);

        var view = new SkosManager(_fx.Store).BuildView(zh);
        var scheme = Assert.Single(view.Schemes);
        Assert.Equal("泵系统术语表", scheme.Title);
        Assert.Equal("zh-CN", scheme.DefaultLanguage);
    }

    [Fact]
    public void Sync_without_tbox_classes_creates_nothing()
    {
        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(0, result.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(0, view.Stats.SchemeCount);
        Assert.Equal(0, view.Stats.ConceptCount);
    }

    // ------------------------------------------------------------------
    // P3-1 (terminology proposals): the deterministic sync now stamps the
    // resolved scheme IRI onto the result so the orchestrator can feed it
    // to the scoped TerminologyAgent.SuggestAsync pass that follows.
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_sets_scheme_iri_when_default_scheme_is_seeded()
    {
        SeedClasses("Pump");

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal($"{_ks.VocabularyGraph}#scheme-extracted", result.SchemeIri);
    }

    [Fact]
    public void Sync_sets_scheme_iri_when_reusing_existing_scheme()
    {
        SeedClasses("Pump");
        var svc = new TerminologyService(_fx.Store);

        // First pass seeds the scheme; second pass reuses it (no new
        // terms). Both must report the scheme IRI so the agent step the
        // orchestrator appends can always feed a non-null scheme.
        svc.SyncAsync(_ks, CancellationToken.None);
        var second = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Equal($"{_ks.VocabularyGraph}#scheme-extracted", second.SchemeIri);
    }

    [Fact]
    public void Sync_leaves_scheme_iri_null_when_no_entities_to_anchor()
    {
        // TBox is empty — EnsureScheme has nothing to anchor, so the
        // sync short-circuits before resolving a scheme. The orchestrator
        // uses SchemeIri as the gate for the agent step, so null must
        // round-trip cleanly (the proposal stage is skipped).
        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Null(result.SchemeIri);
        Assert.Equal(0, result.TermsAdded);
        Assert.Equal(0, result.ProposalsQueued);
    }

    // ------------------------------------------------------------------
    // P3-10 (vocabulary parity metrics): the deterministic sync now
    // reports the five counters Python's sync_from_ontology summary
    // emits — properties / aliases_added / broader_added /
    // stale_mappings_removed / mapping_conflicts — and performs the
    // graph writes behind them.
    // ------------------------------------------------------------------

    [Fact]
    public void Sync_counts_properties_and_creates_concepts_for_them()
    {
        // Python aggregates `classes + object_properties + data_properties`
        // into one entity list; properties get the same mapped-concept
        // treatment as classes. `Properties` surfaces the property count
        // separately so the audit row can spot class-less TBoxes.
        SeedMutation(
            classes: new[] { "Pump" },
            objectProperties: new[] { "drives" },
            dataProperties: new[] { "maxSpeed" },
            axioms: Array.Empty<AxiomMutation>());

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Properties);
        Assert.Equal(3, result.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(3, view.Stats.ConceptCount);
        Assert.Equal(3, view.Stats.MappedCount);
    }

    [Fact]
    public void Sync_clears_stale_mappings_when_entity_disappears()
    {
        // First pass maps Pump + Motor; then the TBox shrinks to Pump only.
        // The Motor concept survives, but its op:mapsTo triple must be
        // removed (stale_mappings_removed == 1) so a human can remap or
        // deprecate it. Mirrors Python `valid_mapping_iris` pruning.
        SeedClasses("Pump", "Motor");
        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(_ks, CancellationToken.None);

        ReplaceTBox("Pump");
        var second = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(second.Error);
        Assert.Equal(1, second.StaleMappingsRemoved);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var motor = view.Concepts.Single(c => c.DisplayLabel == "Motor");
        Assert.Null(motor.MappedEntityIri);
        var pump = view.Concepts.Single(c => c.DisplayLabel == "Pump");
        Assert.NotNull(pump.MappedEntityIri);
    }

    [Fact]
    public void Sync_adds_entity_label_as_alias_when_pref_label_differs()
    {
        // A manually-curated concept is mapped to the Pump class but its
        // pref label is "Fluid Mover". The entity label "Pump" is not
        // attached to the concept yet — the sync must attach it as an
        // skos:altLabel (aliases_added == 1) without touching the curated
        // pref label. Mirrors Python's `existing_keys / label_owner` loop.
        SeedClasses("Pump");
        var manager = new SkosManager(_fx.Store);
        // The sync's EnsureScheme will reuse this pre-created default scheme.
        SeedDefaultScheme(manager);
        var pumpIri = $"{_ks.BaseIri}Pump";
        manager.CreateConcept(_ks,
            $"{_ks.VocabularyGraph}#scheme-extracted",
            new SkosConceptData(
                Iri: $"{_ks.VocabularyGraph}#concept-FluidMover",
                PrefLabel: "Fluid Mover",
                Language: "en",
                MappedEntityIri: pumpIri));

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.AliasesAdded);
        Assert.Equal(0, result.TermsAdded);

        var view = manager.BuildView(_ks);
        var concept = view.Concepts.Single(c => c.MappedEntityIri == pumpIri);
        Assert.Equal("Fluid Mover", concept.DisplayLabel);
        var alias = Assert.Single(concept.AltLabels);
        Assert.Equal("Pump", alias.Value);
    }

    [Fact]
    public void Sync_seeds_broader_from_subclass_relations()
    {
        // "Centrifugal Pump" subclasses "Pump". Both concepts exist after
        // sync; the child must gain a skos:broader triple pointing at the
        // parent concept (broader_added == 1). Mirrors Python's OWL
        // subclass seeding pass.
        SeedMutation(
            classes: new[] { "Pump", "Centrifugal Pump" },
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: new[] { new AxiomMutation("subclass", Sub: "Centrifugal Pump", Super: "Pump") });

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.BroaderAdded);
        Assert.Equal(2, result.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var child = view.Concepts.Single(c => c.DisplayLabel == "Centrifugal Pump");
        var parent = view.Concepts.Single(c => c.DisplayLabel == "Pump");
        Assert.Contains(parent.Iri, child.Broader);
    }

    [Fact]
    public void Sync_reports_mapping_conflict_when_label_owned_by_other_mapping()
    {
        // A pre-existing concept "Pump" is already mapped to the "Other"
        // class's ontology IRI (a VALID mapping target, so the stale pass
        // leaves it alone). The TBox class "Pump" collides with it: the
        // sync must not create a second "Pump" concept, must not remap the
        // curated one, and must report mapping_conflicts == 1 (Python's
        // `elif exact: mapping_conflicts += 1; continue`).
        SeedClasses("Pump", "Other");
        var manager = new SkosManager(_fx.Store);
        SeedDefaultScheme(manager);
        var otherIri = $"{_ks.BaseIri}Other";
        manager.CreateConcept(_ks,
            $"{_ks.VocabularyGraph}#scheme-extracted",
            new SkosConceptData(
                Iri: $"{_ks.VocabularyGraph}#concept-ExternalPump",
                PrefLabel: "Pump",
                Language: "en",
                MappedEntityIri: otherIri));

        var svc = new TerminologyService(_fx.Store);
        var result = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.MappingConflicts);
        Assert.Equal(0, result.TermsAdded);

        var view = manager.BuildView(_ks);
        var concept = view.Concepts.Single(c => c.DisplayLabel == "Pump");
        Assert.Equal(otherIri, concept.MappedEntityIri);
    }

    [Fact]
    public void Sync_reports_zero_new_metrics_on_idempotent_rerun()
    {
        // Re-running the sync on an unchanged TBox must report zero for
        // every P3-10 counter — the pass is fully idempotent.
        SeedMutation(
            classes: new[] { "Pump", "Centrifugal Pump" },
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: new[] { new AxiomMutation("subclass", Sub: "Centrifugal Pump", Super: "Pump") });

        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(_ks, CancellationToken.None);
        var second = svc.SyncAsync(_ks, CancellationToken.None);

        Assert.Null(second.Error);
        Assert.Equal(0, second.TermsAdded);
        Assert.Equal(0, second.AliasesAdded);
        Assert.Equal(0, second.BroaderAdded);
        Assert.Equal(0, second.StaleMappingsRemoved);
        Assert.Equal(0, second.MappingConflicts);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void SeedClasses(params string[] labels) =>
        SeedMutation(
            classes: labels,
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: Array.Empty<AxiomMutation>());

    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }

    /// <summary>
    /// Pre-create the default extracted scheme so tests that hand-place a
    /// concept in the vocabulary graph (alias / conflict fixtures) can
    /// anchor it before the sync runs.
    /// </summary>
    private void SeedDefaultScheme(SkosManager manager) =>
        manager.CreateScheme(_ks, new SkosSchemeData(
            Iri: $"{_ks.VocabularyGraph}#scheme-extracted",
            Title: "Pump systems terminology",
            DefaultLanguage: "en",
            Origin: "extraction"));

    /// <summary>
    /// Replace the TBox graph wholesale (used by the stale-mapping test to
    /// simulate an entity disappearing between two sync passes).
    /// </summary>
    private void ReplaceTBox(params string[] labels)
    {
        var existing = _fx.Store.Match(graphIri: _ks.TBoxGraph);
        if (existing.Count > 0)
        {
            _fx.Store.RemoveQuads(new OntoNamedNode(_ks.TBoxGraph), existing);
        }
        SeedClasses(labels);
    }
}
