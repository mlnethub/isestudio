using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Fixture for the byte-vs-quad consistency test. Owns a temp dir +
/// StoreWrapper, both cleaned up on dispose.
/// </summary>
public sealed class ConflictDetectorFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public ConflictDetectorFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
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

/// <summary>
/// <see cref="ConflictDetector"/> produces a stable signature for a triple
/// set regardless of insertion order. Two captures of the same logical
/// triple set must hash to the same SHA-256; different sets must not.
/// </summary>
public class ConflictDetectorTests : IClassFixture<ConflictDetectorFixture>
{
    private readonly ConflictDetectorFixture _fx;

    public ConflictDetectorTests(ConflictDetectorFixture fx) { _fx = fx; }

    private static OntoQuad MakeQuad(string s, string p, string o, string graph) =>
        new(new OntoNamedNode(s), new OntoNamedNode(p),
            new OntoLiteral(o), new OntoNamedNode(graph));

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_is_identical_for_two_insertion_orders()
    {
        var a = MakeQuad("urn:s1", "urn:p", "v1", "urn:g");
        var b = MakeQuad("urn:s2", "urn:p", "v2", "urn:g");
        var c = MakeQuad("urn:s3", "urn:p", "v3", "urn:g");

        var order1 = new[] { a, b, c };
        var order2 = new[] { c, a, b };
        var order3 = new[] { b, c, a };

        var s1 = ConflictDetector.Signature(order1);
        var s2 = ConflictDetector.Signature(order2);
        var s3 = ConflictDetector.Signature(order3);

        Assert.Equal(s1, s2);
        Assert.Equal(s2, s3);
        // And of course it's not empty.
        Assert.NotEqual(string.Empty, s1);
        // 64-char hex SHA-256.
        Assert.Equal(64, s1.Length);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_differs_for_different_triple_sets()
    {
        var a = MakeQuad("urn:s1", "urn:p", "v1", "urn:g");
        var b = MakeQuad("urn:s2", "urn:p", "v2", "urn:g");

        var set1 = new[] { a };
        var set2 = new[] { b };

        Assert.NotEqual(
            ConflictDetector.Signature(set1),
            ConflictDetector.Signature(set2));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_is_stable_for_blank_nodes_language_tags_and_datatypes()
    {
        // Two captures: each writes the same triple set with the same
        // blank-node label, language tag, and explicit datatype. The
        // signature must match byte-for-byte.
        var g = new OntoNamedNode("urn:g");
        var bnode = new OntoBlankNode("shape");
        var langLit = new OntoLiteral("hello", Language: "en");
        var dtLit = new OntoLiteral("42", Datatype: OntoLiteral.XsdInteger);

        var quads = new[]
        {
            new OntoQuad(bnode, new OntoNamedNode("urn:p1"), langLit, g),
            new OntoQuad(bnode, new OntoNamedNode("urn:p2"), dtLit, g),
        };

        var s1 = ConflictDetector.Signature(quads);
        var s2 = ConflictDetector.Signature(quads);
        Assert.Equal(s1, s2);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_byte_overload_is_stable_across_byte_normalization()
    {
        // Two N-Quads payloads encoding the same triple set with different
        // line endings (LF vs CRLF) should produce identical signatures.
        var text1 = "<urn:s> <urn:p> <urn:o> <urn:g> .\n";
        var text2 = "<urn:s> <urn:p> <urn:o> <urn:g> .\r\n";
        var s1 = ConflictDetector.Signature(System.Text.Encoding.UTF8.GetBytes(text1));
        var s2 = ConflictDetector.Signature(System.Text.Encoding.UTF8.GetBytes(text2));
        Assert.Equal(s1, s2);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_of_empty_input_is_a_known_value()
    {
        // SHA-256 of the empty string is well-known:
        // e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        var s = ConflictDetector.Signature(Array.Empty<OntoQuad>());
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            s);
    }

    // ------------------------------------------------------------------
    // I-1 regression: the byte overload and the quad overload must produce
    // identical hashes for the same logical content. The byte overload
    // routes N-Quads bytes through Oxigraph's loader and re-serializes
    // each parsed quad through the canonical writer; the quad overload
    // canonicalizes the supplied quads directly. Both should agree.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_is_consistent_between_byte_and_quad_overloads()
    {
        var g = new OntoNamedNode("urn:g");
        _fx.Store.AddQuads(g, new[]
        {
            new OntoQuad(new OntoNamedNode("urn:s1"), new OntoNamedNode("urn:p1"),
                new OntoLiteral("v1"), g),
            new OntoQuad(new OntoNamedNode("urn:s2"), new OntoNamedNode("urn:p2"),
                new OntoLiteral("Pump", Language: "en"), g),
            new OntoQuad(new OntoNamedNode("urn:s3"), new OntoNamedNode("urn:p3"),
                new OntoLiteral("42", Datatype: OntoLiteral.XsdInteger), g),
        });

        var quads = _fx.Store.Match(graph: g);
        var nQuads = _fx.Store.DumpNQuads(g);

        var sigFromQuads = ConflictDetector.Signature(quads);
        var sigFromBytes = ConflictDetector.Signature(nQuads);

        Assert.Equal(sigFromQuads, sigFromBytes);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Signature_byte_overload_agrees_with_quad_overload_for_canonical_NQuads()
    {
        // Same content, two different paths through the API:
        //   1. quads (via Match) → Signature(quads)
        //   2. N-Quads bytes (via DumpNQuads) → Signature(bytes)
        // must yield the same SHA-256. Repeated with a different layer to
        // ensure the equality holds across multiple captures.
        var ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/conflict-i1",
            BaseIri: "http://goodcrew.local/ks/test/conflict-i1/onto#");
        var tbox = new OntoNamedNode(ks.TBoxGraph);
        var abox = new OntoNamedNode(ks.ABoxGraph);

        _fx.Store.AddQuads(tbox, new[]
        {
            new OntoQuad(new OntoNamedNode("urn:class"),
                new OntoNamedNode("http://www.w3.org/2000/01/rdf-schema#label"),
                new OntoLiteral("Pump", Language: "en"), tbox),
        });
        _fx.Store.AddQuads(abox, new[]
        {
            new OntoQuad(new OntoNamedNode("urn:instance"),
                new OntoNamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
                new OntoNamedNode("urn:class"), abox),
        });

        var tboxQuads = _fx.Store.Match(graph: tbox);
        var tboxNQuads = _fx.Store.DumpNQuads(tbox);
        Assert.Equal(
            ConflictDetector.Signature(tboxQuads),
            ConflictDetector.Signature(tboxNQuads));

        var aboxQuads = _fx.Store.Match(graph: abox);
        var aboxNQuads = _fx.Store.DumpNQuads(abox);
        Assert.Equal(
            ConflictDetector.Signature(aboxQuads),
            ConflictDetector.Signature(aboxNQuads));
    }
}