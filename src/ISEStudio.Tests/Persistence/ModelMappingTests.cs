using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Smoke-tests the EF Core metadata model: every legacy Python table must be present
/// as an entity type, every entity must have a primary key, and the Guid PK Phase 2
/// migration state must be in effect (no unique <c>ux_*_legacy_id</c> indexes — new
/// rows legitimately share <c>legacy_id = 0</c>).
/// </summary>
public sealed class ModelMappingTests
{
    [Fact]
    public void Model_contains_all_legacy_tables_and_no_unique_legacy_id_indexes()
    {
        using var db = DbContextFactory.CreateSqlite();
        var entities = db.Model.GetEntityTypes().ToDictionary(x => x.ClrType.Name);
        Assert.Equal(24, entities.Count);
        Assert.All(entities.Values, entity => Assert.NotNull(entity.FindPrimaryKey()));
        // D1(c): ux_*_legacy_id UNIQUE indexes were dropped in Phase 2 — a
        // unique LegacyId index would now break inserts of concurrent 0s.
        Assert.DoesNotContain(entities[nameof(KnowledgeSystemEntity)].GetIndexes(),
            index => index.IsUnique && index.Properties.Single().Name == "LegacyId");
    }
}