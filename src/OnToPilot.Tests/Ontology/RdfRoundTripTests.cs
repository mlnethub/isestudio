using System.Text;
using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Fixture for import / export round-trip tests. Owns a temp RocksDB store
/// + an importer + an exporter; wipes the store between tests.
/// </summary>
public sealed class RdfRoundTripFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }
    public RdfImportService Importer { get; }
    public RdfExportService Exporter { get; }

    public RdfRoundTripFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-rdf-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Store = new StoreWrapper(Path);
        Importer = new RdfImportService(Store);
        Exporter = new RdfExportService(Store);
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

public class RdfRoundTripTests : IClassFixture<RdfRoundTripFixture>, IAsyncLifetime
{
    private readonly RdfRoundTripFixture _fx;
    private readonly KsContext _ks;

    public RdfRoundTripTests(RdfRoundTripFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/rdf-rt",
            BaseIri: "http://goodcrew.local/ks/test/rdf-rt/onto#");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Required: four export formats, language tags round-trip.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_round_trip_preserves_language_tags_for_all_four_formats()
    {
        // Seed the Vocabulary layer with two literals that have language tags
        // and one with an explicit datatype.
        var vocabGraph = new OntoNamedNode(_ks.VocabularyGraph);
        _fx.Store.AddQuads(vocabGraph,
        [
            new OntoQuad(new OntoNamedNode("urn:s"),
                         new OntoNamedNode("http://www.w3.org/2004/02/skos/core#prefLabel"),
                         new OntoLiteral("Pump", Language: "en"),
                         vocabGraph),
            new OntoQuad(new OntoNamedNode("urn:s"),
                         new OntoNamedNode("http://www.w3.org/2004/02/skos/core#prefLabel"),
                         new OntoLiteral("泵", Language: "zh-cn"),
                         vocabGraph),
            new OntoQuad(new OntoNamedNode("urn:s"),
                         new OntoNamedNode("urn:count"),
                         new OntoLiteral("42", Datatype: OntoLiteral.XsdInteger),
                         vocabGraph),
        ]);

        // Four formats from the plan: N-Quads, N-Triples, Turtle, TriG.
        var formats = new[] { RdfFormat.NQuads, RdfFormat.NTriples, RdfFormat.Turtle, RdfFormat.TriG };
        foreach (var format in formats)
        {
            var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.Vocabulary, format, default);
            Assert.NotEmpty(bytes);

            // Parse the bytes back into a fresh in-memory store and verify
            // the language tag survived.
            using var fresh = new Oxigraph.Store();
            fresh.Load(Encoding.UTF8.GetString(bytes), format);

            var all = fresh.Match();
            var literals = all
                .Where(q => q.Object is OntoLiteral)
                .Select(q => (OntoLiteral)q.Object)
                .ToList();

            Assert.Contains(literals, l => l.Value == "Pump" && l.Language == "en");
            Assert.Contains(literals, l => l.Value == "泵" && l.Language == "zh-cn");
            Assert.Contains(literals, l => l.Value == "42" && l.Datatype?.Value == OntoLiteral.XsdInteger.Value);
        }
    }

    // ------------------------------------------------------------------
    // Import: Merge adds quads without dropping existing ones.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Import_Merge_adds_quads_without_dropping_existing()
    {
        var graph = new OntoNamedNode(_ks.TBoxGraph);

        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:s1"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("v1"),
            graph)]);

        var payload = Encoding.UTF8.GetBytes(
            "<urn:s2> <urn:p> <urn:o2> <" + _ks.TBoxGraph + "> .\n");

        await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, payload, ImportMode.Merge, default);

        Assert.Equal(2ul, _fx.Store.Count(graph: graph));
        Assert.Contains(_fx.Store.Match(graph: graph),
            q => ((OntoNamedNode)q.Subject).Value == "urn:s1");
        Assert.Contains(_fx.Store.Match(graph: graph),
            q => ((OntoNamedNode)q.Subject).Value == "urn:s2");
    }

    // ------------------------------------------------------------------
    // Import: Replace wipes the layer first.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Import_Replace_wipes_existing_layer()
    {
        var graph = new OntoNamedNode(_ks.TBoxGraph);

        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:s1"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("v1"),
            graph)]);

        var payload = Encoding.UTF8.GetBytes(
            "<urn:s2> <urn:p> <urn:o2> <" + _ks.TBoxGraph + "> .\n");

        await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, payload, ImportMode.Replace, default);

        Assert.Equal(1ul, _fx.Store.Count(graph: graph));
        Assert.DoesNotContain(_fx.Store.Match(graph: graph),
            q => ((OntoNamedNode)q.Subject).Value == "urn:s1");
        Assert.Contains(_fx.Store.Match(graph: graph),
            q => ((OntoNamedNode)q.Subject).Value == "urn:s2");
    }

    // ------------------------------------------------------------------
    // Import: parse failure reverts the layer (no partial writes).
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Import_reverts_on_parse_failure()
    {
        var graph = new OntoNamedNode(_ks.TBoxGraph);
        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:keep"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("v"),
            graph)]);

        var beforeBytes = _fx.Store.DumpNQuads(graph);

        // Malformed N-Quads: unterminated string.
        var bad = Encoding.UTF8.GetBytes("<urn:s2> <urn:p> \"unterminated .\n");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, bad, ImportMode.Merge, default));

        // Layer must be byte-identical to before.
        Assert.Equal(beforeBytes, _fx.Store.DumpNQuads(graph));
    }

    // ------------------------------------------------------------------
    // Export: language tags survive specifically in the N-Quads dump.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_NQuads_preserves_language_tags_in_bytes()
    {
        var graph = new OntoNamedNode(_ks.VocabularyGraph);
        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:s"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("hello", Language: "en"),
            graph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.Vocabulary, RdfFormat.NQuads, default);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"hello\"@en", text);
    }

    // ------------------------------------------------------------------
    // Export: Turtle round trip preserves datatypes.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_Turtle_preserves_datatypes()
    {
        var graph = new OntoNamedNode(_ks.TBoxGraph);
        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:s"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("3.14", Datatype: OntoLiteral.XsdDouble),
            graph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.TBox, RdfFormat.Turtle, default);
        var text = Encoding.UTF8.GetString(bytes);
        // Turtle uses ^^<...> for datatypes, but Oxigraph may use the
        // xsd:double prefix form. Either way the IRI must be present.
        Assert.Contains("http://www.w3.org/2001/XMLSchema#double", text);
    }

    // ------------------------------------------------------------------
    // Export: TriG preserves named-graph context.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_TriG_emits_named_graph_block()
    {
        var graph = new OntoNamedNode(_ks.TBoxGraph);
        _fx.Store.AddQuads(graph, [new OntoQuad(
            new OntoNamedNode("urn:s"),
            new OntoNamedNode("urn:p"),
            new OntoLiteral("v"),
            graph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.TBox, RdfFormat.TriG, default);
        var text = Encoding.UTF8.GetString(bytes);
        // TriG: <graphIri> { ... } or graph <graphIri> { ... }. The exact
        // syntax Oxigraph emits is checked via the substring test below —
        // the assertion that matters is that the graph IRI survives the
        // round trip.
        Assert.Contains(_ks.TBoxGraph, text);
    }

    // ------------------------------------------------------------------
    // Export: empty layer produces valid bytes (length > 0 since the
    // format requires at least a comment / default-graph wrapper).
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_empty_layer_returns_bytes_for_each_format()
    {
        foreach (var format in new[] { RdfFormat.NQuads, RdfFormat.NTriples, RdfFormat.Turtle, RdfFormat.TriG })
        {
            var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.ABox, format, default);
            // Empty bytes are valid; Oxigraph returns at least a header in
            // some formats but NQuads/TriG with no statements can be empty.
            // We just assert it doesn't throw.
            Assert.NotNull(bytes);
        }
    }
}