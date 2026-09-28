using ISEStudio.Ontology;

namespace ISEStudio.Tests.Ontology;

public sealed class FactKeyTests
{
    [Fact]
    public void DataKey_bounds_long_values_to_the_database_key_limit()
    {
        var key = FactKey.DataKey("urn:s", "urn:p", new string('x', 2048));

        Assert.InRange(key.Length, 1, 1024);
        Assert.StartsWith("data|", key, StringComparison.Ordinal);
        Assert.Equal(key, FactKey.DataKey("urn:s", "urn:p", new string('x', 2048)));
    }
}
