using OnToPilot.Application.Foundation;
using OnToPilot.Ontology;
using Xunit;

namespace OnToPilot.Tests.Ontology;

public sealed class OntologyViewBuilderTests
{
    [Fact]
    public async Task BuildFromStoreAsync_with_null_store_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store: null, graphIri: "http://x/graph", CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Empty(view.ObjectProperties);
        Assert.Empty(view.DataProperties);
        Assert.Empty(view.Axioms.SubclassOf);
        Assert.Empty(view.Axioms.DisjointWith);
        Assert.Empty(view.Axioms.EquivalentClass);
        Assert.Empty(view.Labels);
        Assert.Equal(0, view.Stats.ClassCount);
        Assert.Equal(0, view.Stats.PropertyCount);
        Assert.Equal(0, view.Stats.AxiomCount);
        Assert.Null(view.KnowledgeSystem);
    }

    [Fact]
    public async Task BuildFromNQuadsAsync_with_empty_bytes_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromNQuadsAsync(
            tboxShard: Array.Empty<byte>(), CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Equal(0, view.Stats.ClassCount);
    }
}