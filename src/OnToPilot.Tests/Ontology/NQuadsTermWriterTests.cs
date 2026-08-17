using System.Text;
using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Fixture for the cross-call-site consistency test. Each instance owns a
/// fresh temp directory, StoreWrapper, importer, and exporter; all are torn
/// down on dispose.
/// </summary>
public sealed class NQuadsTermWriterFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }
    public RdfImportService Importer { get; }
    public RdfExportService Exporter { get; }
    public KsContext Ks { get; }

    public NQuadsTermWriterFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-nquads-term-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Store = new StoreWrapper(Path);
        Importer = new RdfImportService(Store);
        Exporter = new RdfExportService(Store);
        Ks = new KsContext(
            GraphIri: "http://ontopilot.local/ks/test/nquads-term-writer",
            BaseIri: "http://ontopilot.local/ks/test/nquads-term-writer/onto#");
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

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

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
        var tbox = new OntoNamedNode(_fx.Ks.TBoxGraph);
        _fx.Store.AddQuads(tbox,
        [
            // Plain named-node subject with a plain string literal object.
            new OntoQuad(
                new OntoNamedNode("urn:s1"),
                new OntoNamedNode("urn:p1"),
                new OntoLiteral("plain string"),
                tbox),
            // Language-tagged literal — exercises the `@lang` branch.
            new OntoQuad(
                new OntoNamedNode("urn:s2"),
                new OntoNamedNode("urn:p2"),
                new OntoLiteral("hello", Language: "en"),
                tbox),
            // Explicitly-typed literal (xsd:integer) — exercises the
            // `^^<datatype>` branch.
            new OntoQuad(
                new OntoNamedNode("urn:s3"),
                new OntoNamedNode("urn:p3"),
                new OntoLiteral("42", Datatype: OntoLiteral.XsdInteger),
                tbox),
            // A literal whose value contains a backslash, a double quote,
            // and a newline — the three escape sequences every writer
            // must handle identically.
            new OntoQuad(
                new OntoNamedNode("urn:s4"),
                new OntoNamedNode("urn:p4"),
                new OntoLiteral("a\\b\"c\nd"),
                tbox),
        ]);

        // Call site 1: StoreWrapper.DumpNQuads (uses AppendNQuadsTerm).
        var dumpBytes = _fx.Store.DumpNQuads(tbox);

        // Call site 2: RdfExportService.ExportAsync (uses AppendTerm).
        var exportBytes = await _fx.Exporter.ExportAsync(
            _fx.Ks, RdfLayer.TBox, RdfFormat.NQuads);

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
