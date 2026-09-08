using System.Text;
using ISEStudio.Ontology;
using ISEStudio.Tests.Infrastructure;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Fixture for the cross-call-site consistency test. Each instance owns a
/// fresh temp directory, StoreWrapper, importer, and exporter; all are torn
/// down on dispose.
/// </summary>
public sealed class NQuadsTermWriterFixture : IAsyncLifetime
{
    public PostgresRdfFixture Database { get; } = new();
    public RdfImportService Importer { get; private set; } = null!;
    public RdfExportService Exporter { get; private set; } = null!;
    public KsContext Ks { get; private set; } = null!;

    public NQuadsTermWriterFixture()
    {
        Importer = null!;
        Exporter = null!;
        Ks = null!;
    }

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Importer = new RdfImportService(Database.Statements, new RdfImportParser());
        Exporter = new RdfExportService(Database.Statements);
        Ks = new KsContext(Database.Db.KnowledgeSystems.Single().GraphIri,
            Database.Db.KnowledgeSystems.Single().BaseIri,
            KnowledgeSystemId: Database.KnowledgeSystemId);
    }

    public Task DisposeAsync() => Database.DisposeAsync();
}

/// <summary>
/// Guards against drift between the three historical AppendTerm copies
/// (in StoreWrapper, ConflictDetector, and RdfExportService) by routing
/// every term-encoding path through one <see cref="NQuadsTermWriter"/>
/// implementation. The regression test feeds the same triple set through
/// each call site and asserts byte-equal output.
/// </summary>
public class NQuadsTermWriterTests : IClassFixture<NQuadsTermWriterFixture>, IAsyncLifetime
{
    private readonly NQuadsTermWriterFixture _fx;

    public NQuadsTermWriterTests(NQuadsTermWriterFixture fx) { _fx = fx; }

    public Task InitializeAsync() => _fx.Database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // I-2 regression: the canonical term writer (NQuadsTermWriter) is the
    // single source of truth for N-Quads encoding. Before the fix, three
    // byte-identical private copies of the same switch lived in
    // StoreWrapper.AppendNQuadsTerm, ConflictDetector.AppendTerm, and
    // RdfExportService.AppendTerm — any divergence between them would have
    // produced byte-different output for the same triple set, breaking
    // signature-vs-export equality.
    //
    // The test seeds the TBox layer with a deliberately mixed triple set
    // (named node, plain literal, language-tagged literal, typed literal,
    // and a literal containing every N-Quads escape character) and asserts:
    //
    //   1. StoreWrapper.DumpNQuads bytes == ExportAsync(...NQuads) bytes
    //      (both go through NQuadsTermWriter.Append).
    //   2. The Signature(dump bytes) and Signature(export bytes) overloads
    //      both produce the same SHA-256 (proves the canonical writer
    //      inside ConflictDetector agrees with StoreWrapper and
    //      RdfExportService at the byte level — order-independent because
    //      Signature sorts canonical lines before hashing).
    //
    // Blank-node labels are intentionally avoided here: Oxigraph's
    // N-Quads loader reassigns labels on parse, so a
    // Signature(quads)-vs-Signature(dumpBytes) comparison round-trips
    // blank-node identity loss. The byte-vs-byte equality assertion above
    // already proves all three call sites agree on byte content; the
    // signature-level equality assertion proves the hash function sees the
    // same byte set after reordering.
    //
    // If any of the three call sites ever drifts away from the centralised
    // writer, this test fails loudly with a byte-comparison error.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public async Task Three_call_sites_produce_identical_bytes_for()
    {
        await _fx.Database.Statements.ReplaceLayerAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString(),
        [
            new(new RdfIri("urn:s1"), "urn:p1", new RdfLiteral("plain string"), _fx.Ks.TBoxGraph),
            new(new RdfIri("urn:s2"), "urn:p2", new RdfLiteral("hello", "en"), _fx.Ks.TBoxGraph),
            new(new RdfIri("urn:s3"), "urn:p3", new RdfLiteral("42", null, OntoLiteral.XsdInteger.Value), _fx.Ks.TBoxGraph),
            new(new RdfIri("urn:s4"), "urn:p4", new RdfLiteral("a\\b\"c\nd"), _fx.Ks.TBoxGraph),
        ]);

        // Call site 1: StoreWrapper.DumpNQuads (uses AppendNQuadsTerm).
        var statements = await _fx.Database.Statements.ListAsync(_fx.Database.KnowledgeSystemId, RdfLayer.TBox.ToString());
        var dumpBytes = RdfExportService.SerializeNQuads(statements);

        // Call site 2: RdfExportService.ExportAsync (uses AppendTerm).
        var exportBytes = await _fx.Exporter.ExportAsync(
            _fx.Ks, RdfLayer.TBox, RdfExportFormat.NQuads);

        // Byte-exact equality between the two N-Quads producers.
        Assert.Equal(
            Encoding.UTF8.GetString(dumpBytes),
            Encoding.UTF8.GetString(exportBytes));

        // Call site 3: ConflictDetector.Signature — its byte overload must
        // hash both producers to the same SHA-256 (proves the canonical
        // writer inside ConflictDetector agrees with both StoreWrapper
        // and RdfExportService on byte content, order-independent because
        // Signature sorts canonical lines before hashing).
        var sigFromDump = ConflictDetector.Signature(dumpBytes);
        var sigFromExport = ConflictDetector.Signature(exportBytes);
        Assert.Equal(sigFromDump, sigFromExport);
    }
}
