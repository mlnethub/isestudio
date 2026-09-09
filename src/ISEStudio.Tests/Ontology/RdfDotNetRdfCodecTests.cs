using ISEStudio.Ontology;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Codec-level tests for the dotNetRDF → RdfStatement boundary. Every RDF
/// document boundary that crosses into or out of ISEStudio's runtime must
/// go through <see cref="RdfDotNetRdfCodec"/>, so its fidelity is the
/// single point of failure for term round-trips, language tags, datatypes,
/// and graph IRIs.
/// </summary>
public class RdfDotNetRdfCodecTests
{
    [Fact]
    [Trait("Category", "RdfCore")]
    public void NQuads_round_trip_preserves_terms_and_graph()
    {
        var input = new RdfStatement[]
        {
            new(new RdfBlankNode("b1"), "urn:p", new RdfLiteral("a\\b\"c\nd", "en"), "urn:g"),
            new(new RdfIri("urn:s"), "urn:count", new RdfLiteral("42", null,
                "http://www.w3.org/2001/XMLSchema#integer"), "urn:g"),
        };

        var actual = RdfDotNetRdfCodec.ParseNQuads(RdfDotNetRdfCodec.SerializeNQuads(input));
        Assert.Equal(input.ToHashSet(), actual.Statements.ToHashSet());
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void NQuads_serialize_emits_per_statement_graph_iri()
    {
        var statements = new[]
        {
            new RdfStatement(new RdfIri("urn:s"), "urn:p", new RdfIri("urn:o"), "urn:g1"),
            new RdfStatement(new RdfIri("urn:s"), "urn:p", new RdfIri("urn:o"), "urn:g2"),
        };

        var bytes = RdfDotNetRdfCodec.SerializeNQuads(statements);
        var roundTripped = RdfDotNetRdfCodec.ParseNQuads(bytes).Statements;

        Assert.Equal(statements.ToHashSet(), roundTripped.ToHashSet());
    }
}