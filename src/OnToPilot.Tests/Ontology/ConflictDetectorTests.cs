using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// <see cref="ConflictDetector"/> produces a stable signature for a triple
/// set regardless of insertion order. Two captures of the same logical
/// triple set must hash to the same SHA-256; different sets must not.
/// </summary>
public class ConflictDetectorTests
{
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
}