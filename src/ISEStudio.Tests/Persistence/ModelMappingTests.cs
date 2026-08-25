using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Smoke-tests the EF Core metadata model: every legacy Python table must be present
/// as an entity type, every entity must have a primary key, and key uniqueness constraints
/// from the Python contract must round-trip into EF indexes.
/// </summary>
public sealed class ModelMappingTests
{
    [Fact]
    public void Model_contains_all_legacy_tables_and_compatibility_keys()
    {
        using var db = DbContextFactory.CreateSqlite();
        var entities = db.Model.GetEntityTypes().ToDictionary(x => x.ClrType.Name);
        Assert.Equal(24, entities.Count);
        Assert.All(entities.Values, entity => Assert.NotNull(entity.FindPrimaryKey()));
        Assert.Contains(entities[nameof(KnowledgeSystemEntity)].GetIndexes(),
            index => index.IsUnique && index.Properties.Single().Name == "LegacyId");
    }
}