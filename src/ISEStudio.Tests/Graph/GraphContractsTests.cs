using ISEStudio.Graph;

namespace ISEStudio.Tests.Graph;

public sealed class GraphContractsTests
{
    [Fact]
    public void Record_fact_command_requires_exactly_one_object_representation()
    {
        var command = new RecordFactCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), GraphObjectKind.Entity,
            Guid.NewGuid(), "must-not-be-present", 0.9m, null, null,
            DateTimeOffset.UtcNow, [], null);

        Assert.Throws<ArgumentException>(() => command.Validate());
    }
}