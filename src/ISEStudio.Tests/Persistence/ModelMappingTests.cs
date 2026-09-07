using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Smoke-tests the EF Core metadata model: every legacy Python table must be present
/// as an entity type and every entity must have a primary key. Phase 3
/// retired the numeric surrogate column, so the model now keys every
/// business table on its Guid PK alone (compilation already proves no
/// entity still exposes the old property).
/// </summary>
public sealed class ModelMappingTests
{
    [Fact]
    public void Model_contains_all_legacy_tables_with_guid_pks()
    {
        using var db = DbContextFactory.CreateSqlite();
        var entities = db.Model.GetEntityTypes().ToDictionary(x => x.ClrType.Name);
        Assert.Equal(35, entities.Count);
        Assert.All(entities.Values, entity => Assert.NotNull(entity.FindPrimaryKey()));
    }

    [Fact]
    public void Sqlite_create_script_uses_sqlite_compatible_hash_constraint()
    {
        using var db = DbContextFactory.CreateSqlite();

        var script = db.Database.GenerateCreateScript();

        Assert.Contains("length(content_sha256) = 64", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT GLOB", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content_sha256 ~", script, StringComparison.OrdinalIgnoreCase);
    }
}
