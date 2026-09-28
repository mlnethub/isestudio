using ISEStudio.Extraction;
using ISEStudio.Ontology;
using ISEStudio.Tests.Infrastructure;

namespace ISEStudio.Tests.Extraction;

public sealed class ExtractionMergerLabelTests : IClassFixture<PostgresRdfFixture>, IAsyncLifetime
{
    private readonly PostgresRdfFixture _fixture;
    private readonly KsContext _ks;

    public ExtractionMergerLabelTests(PostgresRdfFixture fixture)
    {
        _fixture = fixture;
        _ks = new KsContext(
            $"http://goodcrew.local/ks/test/{fixture.KnowledgeSystemId:N}",
            $"http://goodcrew.local/ks/test/{fixture.KnowledgeSystemId:N}/onto#",
            KnowledgeSystemId: fixture.KnowledgeSystemId);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void MergeABox_persists_the_extracted_individual_label()
    {
        var classIri = _ks.BaseIri + "Person";
        _fixture.TBox.AddStatements(_ks.TBoxGraph, new[]
        {
            new RdfStatement(new RdfIri(classIri), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlClass), _ks.TBoxGraph),
            new RdfStatement(new RdfIri(classIri), Vocabulary.RdfsLabel,
                new RdfLiteral("Person"), _ks.TBoxGraph),
        });

        var merger = new ExtractionMerger(_fixture.Statements);
        merger.MergeABox(_ks, new ABoxDelta(new[]
        {
            new AboxIndividual(
                "Alice",
                "Person",
                Evidence: null,
                Attributes: Array.Empty<AboxAttribute>(),
                Relations: Array.Empty<AboxRelation>()),
        }));

        var labels = _fixture.ABox.Match(predicateIri: Vocabulary.RdfsLabel, graphIri: _ks.ABoxGraph);
        Assert.Contains(labels, statement =>
            statement.Object is RdfLiteral literal && literal.Value == "Alice");
    }
}
