using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class TerminologyInputsTests
{
    [Fact]
    public void TerminologyInput_EmptyConstruction_RoundTrips()
    {
        var input = new TerminologyInput(
            Ks: new KsContext("http://g/ks", "http://g/ks/onto#"),
            KnowledgeSystemId: Guid.Empty,
            Model: null,
            SuggestEnabled: false);

        Assert.Equal(Guid.Empty, input.KnowledgeSystemId);
        Assert.Null(input.Model);
        Assert.False(input.SuggestEnabled);
        Assert.Equal("http://g/ks/onto#", input.Ks.BaseIri);
        Assert.Equal("http://g/ks", input.Ks.TBoxGraph);
    }

    [Fact]
    public void TermSyncCarry_DefaultConstruction_AllZero()
    {
        var carry = new TermSyncCarry(null, null, null, 0);

        Assert.Null(carry.SchemeIri);
        Assert.Null(carry.View);
        Assert.Null(carry.PreView);
        Assert.Equal(0, carry.PropertyCount);
        Assert.Equal(0, carry.StaleMappingsRemoved);
        Assert.Equal(0, carry.TermsAdded);
        Assert.Equal(0, carry.TermsMapped);
        Assert.Equal(0, carry.MappingConflicts);
        Assert.Equal(0, carry.AliasesAdded);
        Assert.Equal(0, carry.BroaderAdded);
        Assert.Null(carry.Error);
        Assert.False(carry.Skipped);
    }
}
