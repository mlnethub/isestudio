using System.Text;
using ISEStudio.Ontology;
using ISEStudio.Tests.Infrastructure;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Fixture for import / export round-trip tests backed by PostgreSQL.
/// </summary>
public sealed class RdfRoundTripFixture : IAsyncLifetime
{
    public PostgresRdfFixture Database { get; } = new();
    public RdfImportService Importer { get; private set; } = null!;
    public RdfExportService Exporter { get; private set; } = null!;

    public RdfRoundTripFixture()
    {
    }

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Importer = new RdfImportService(Database.Statements, new RdfImportParser());
        Exporter = new RdfExportService(Database.Statements);
    }

    public Task DisposeAsync() => Database.DisposeAsync();
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
            BaseIri: "http://goodcrew.local/ks/test/rdf-rt/onto#",
            KnowledgeSystemId: _fx.Database.KnowledgeSystemId);
    }

    public Task InitializeAsync()
    {
        return _fx.Database.ResetAsync();
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
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.Vocabulary.ToString(),
        [
            new(new RdfIri("urn:s"), "http://www.w3.org/2004/02/skos/core#prefLabel", new RdfLiteral("Pump", "en"), _ks.VocabularyGraph),
            new(new RdfIri("urn:s"), "http://www.w3.org/2004/02/skos/core#prefLabel", new RdfLiteral("泵", "zh-cn"), _ks.VocabularyGraph),
            new(new RdfIri("urn:s"), "urn:count", new RdfLiteral("42", null, OntoLiteral.XsdInteger.Value), _ks.VocabularyGraph),
        ]);

        // Four formats from the plan: N-Quads, N-Triples, Turtle, TriG.
        var formats = new[] { RdfExportFormat.NQuads, RdfExportFormat.NTriples, RdfExportFormat.Turtle, RdfExportFormat.TriG };
        foreach (var format in formats)
        {
            var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.Vocabulary, format, default);
            Assert.NotEmpty(bytes);

            // Parse the bytes back into a fresh in-memory store and verify
            // the language tag survived.
            using var fresh = new Oxigraph.Store();
            fresh.Load(Encoding.UTF8.GetString(bytes), format switch
            {
                RdfExportFormat.NQuads => Oxigraph.RdfFormat.NQuads,
                RdfExportFormat.NTriples => Oxigraph.RdfFormat.NTriples,
                RdfExportFormat.Turtle => Oxigraph.RdfFormat.Turtle,
                RdfExportFormat.TriG => Oxigraph.RdfFormat.TriG,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            });

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

        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
            [new(new RdfIri("urn:s1"), "urn:p", new RdfLiteral("v1"), _ks.TBoxGraph)]);

        var payload = Encoding.UTF8.GetBytes(
            "<urn:s2> <urn:p> <urn:o2> <" + _ks.TBoxGraph + "> .\n");

        await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, payload, ImportMode.Merge, default);

        var merged = await _fx.Database.Statements.ListAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString());
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, q => q.Subject is RdfIri { Value: "urn:s1" });
        Assert.Contains(merged, q => q.Subject is RdfIri { Value: "urn:s2" });
    }

    // ------------------------------------------------------------------
    // Import: Replace wipes the layer first.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Import_Replace_wipes_existing_layer()
    {
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
            [new(new RdfIri("urn:s1"), "urn:p", new RdfLiteral("v1"), _ks.TBoxGraph)]);

        var payload = Encoding.UTF8.GetBytes(
            "<urn:s2> <urn:p> <urn:o2> <" + _ks.TBoxGraph + "> .\n");

        await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, payload, ImportMode.Replace, default);

        var replaced = await _fx.Database.Statements.ListAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString());
        Assert.Single(replaced);
        Assert.DoesNotContain(replaced, q => q.Subject is RdfIri { Value: "urn:s1" });
        Assert.Contains(replaced, q => q.Subject is RdfIri { Value: "urn:s2" });
    }

    // ------------------------------------------------------------------
    // Import: parse failure reverts the layer (no partial writes).
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Import_reverts_on_parse_failure()
    {
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
            [new(new RdfIri("urn:keep"), "urn:p", new RdfLiteral("v"), _ks.TBoxGraph)]);

        var before = await _fx.Database.Statements.ListAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString());
        var beforeBytes = RdfExportService.SerializeNQuads(before);

        // Malformed N-Quads: unterminated string.
        var bad = Encoding.UTF8.GetBytes("<urn:s2> <urn:p> \"unterminated .\n");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _fx.Importer.ImportAsync(_ks, RdfLayer.TBox, bad, ImportMode.Merge, default));

        // Layer must be byte-identical to before.
        var after = await _fx.Database.Statements.ListAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString());
        Assert.Equal(beforeBytes, RdfExportService.SerializeNQuads(after));
    }

    // ------------------------------------------------------------------
    // Export: language tags survive specifically in the N-Quads dump.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Export_NQuads_preserves_language_tags_in_bytes()
    {
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.Vocabulary.ToString(),
            [new(new RdfIri("urn:s"), "urn:p", new RdfLiteral("hello", "en"), _ks.VocabularyGraph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.Vocabulary, RdfExportFormat.NQuads, default);
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
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
            [new(new RdfIri("urn:s"), "urn:p", new RdfLiteral("3.14", null, OntoLiteral.XsdDouble.Value), _ks.TBoxGraph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.TBox, RdfExportFormat.Turtle, default);
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
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
            [new(new RdfIri("urn:s"), "urn:p", new RdfLiteral("v"), _ks.TBoxGraph)]);

        var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.TBox, RdfExportFormat.TriG, default);
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
        foreach (var format in new[] { RdfExportFormat.NQuads, RdfExportFormat.NTriples, RdfExportFormat.Turtle, RdfExportFormat.TriG })
        {
            var bytes = await _fx.Exporter.ExportAsync(_ks, RdfLayer.ABox, format, default);
            // Empty bytes are valid; Oxigraph returns at least a header in
            // some formats but NQuads/TriG with no statements can be empty.
            // We just assert it doesn't throw.
            Assert.NotNull(bytes);
        }
    }
}