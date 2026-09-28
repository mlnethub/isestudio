using ISEStudio.Ontology;

namespace ISEStudio.Tests.Ontology;

public sealed class ConflictSignatureTests
{
    [Fact]
    public void Long_cycle_signature_is_bounded_for_persistence()
    {
        const string graph = "http://example.test/graph";
        var first = "http://example.test/class/" + new string('a', 700);
        var second = "http://example.test/class/" + new string('b', 700);
        var statements = new[]
        {
            new RdfStatement(new RdfIri(first), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlClass), graph),
            new RdfStatement(new RdfIri(second), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlClass), graph),
            new RdfStatement(new RdfIri(first), Vocabulary.RdfsSubClassOf,
                new RdfIri(second), graph),
            new RdfStatement(new RdfIri(second), Vocabulary.RdfsSubClassOf,
                new RdfIri(first), graph),
        };

        var conflict = Assert.Single(ConflictDetection.Detect(statements, graph));

        Assert.InRange(conflict.Signature.Length, 1, 1024);
        Assert.StartsWith("cycle|", conflict.Signature, StringComparison.Ordinal);
    }
}
