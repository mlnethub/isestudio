using System.Text;
using ISEStudio.Ontology;
using Oxigraph;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Ontology;

public sealed class RdfImportParserTests
{
    private readonly RdfImportParser _parser = new();

    [Theory]
    [InlineData("ttl", "turtle")]
    [InlineData("rdf/xml", "rdfxml")]
    [InlineData("nt", "ntriples")]
    [InlineData("json-ld", "jsonld")]
    [InlineData("auto", "auto")]
    public void NormalizeFormat_maps_aliases_to_canonical_names(string requested, string expected)
    {
        Assert.Equal(expected, RdfImportParser.NormalizeFormat(requested));
    }

    [Fact]
    public void Parse_auto_uses_file_extension_before_sniffing()
    {
        var bytes = Encoding.UTF8.GetBytes("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class .");

        var parsed = _parser.Parse(bytes, "pump.ttl", "auto", "urn:base:", 10, "scope");

        Assert.Equal("turtle", parsed.Format);
        Assert.Single(parsed.Triples);
    }

    [Fact]
    public void Parse_rejects_empty_files()
    {
        var ex = Assert.Throws<RdfImportException>(() =>
            _parser.Parse(Array.Empty<byte>(), "empty.ttl", "auto", null, 10, "scope"));

        Assert.Equal("The RDF file is empty", ex.Message);
    }

    [Fact]
    public void Parse_enforces_max_triples()
    {
        var bytes = Encoding.UTF8.GetBytes("<urn:s1> <urn:p> <urn:o> .\n<urn:s2> <urn:p> <urn:o> .");

        var ex = Assert.Throws<RdfImportException>(() =>
            _parser.Parse(bytes, "data.nt", "ntriples", null, 1, "scope"));

        Assert.Equal("RDF file exceeds the 1-triple import limit", ex.Message);
    }

    [Fact]
    public void Parse_scopes_blank_nodes_deterministically()
    {
        var bytes = Encoding.UTF8.GetBytes("_:b0 <urn:p> <urn:o> .");

        var parsed = _parser.Parse(bytes, "data.nt", "ntriples", null, 10, "abc123");

        var subject = Assert.IsType<OntoBlankNode>(parsed.Triples.Single().Subject);
        Assert.Contains("rdfimport_abc123_0", subject.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Partition_auto_places_schema_nodes_in_tbox_and_instances_in_abox()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
            <urn:Pump> a owl:Class .
            <urn:p101> rdf:type <urn:Pump> .
            """);
        var parsed = _parser.Parse(bytes, "mixed.ttl", "turtle", null, 10, "scope");

        var partition = _parser.Partition(parsed.Triples, "auto");

        Assert.Single(partition.TBox);
        Assert.Single(partition.ABox);
        Assert.Contains(partition.TBox, t => ((OntoNamedNode)t.Subject).Value == "urn:Pump");
        Assert.Contains(partition.ABox, t => ((OntoNamedNode)t.Subject).Value == "urn:p101");
    }
}