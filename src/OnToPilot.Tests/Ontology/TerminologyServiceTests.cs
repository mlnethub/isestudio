using OnToPilot.Extraction;
using OnToPilot.Ontology;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using KsContext = OnToPilot.Ontology.KsContext;

namespace OnToPilot.Tests.Ontology;

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
            "ontopilot-term-" + Guid.NewGuid().ToString("N"));
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
    // Helpers
    // ------------------------------------------------------------------

    private void SeedClasses(params string[] labels)
    {
        var mutation = new OntologyMutation(
            Classes: labels.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: Array.Empty<PropertyMutation>(),
            DataProperties: Array.Empty<PropertyMutation>(),
            Axioms: Array.Empty<AxiomMutation>());
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }
}
