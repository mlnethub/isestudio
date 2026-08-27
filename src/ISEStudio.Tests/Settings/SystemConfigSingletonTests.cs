using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Settings;

/// <summary>
/// Phase 3: the SystemConfig singleton is enforced by a partial UNIQUE
/// INDEX on <c>IsSingleton</c> (<c>ux_systemconfig_singleton</c>). These
/// tests pin the entity-level invariants against SQLite; the unique
/// index itself is only exercised on PostgreSQL (SQLite ignores
/// <c>HasFilter</c>), so that half lives in
/// <see cref="ISEStudio.IntegrationTests.Persistence.PostgresSchemaTests"/>.
/// </summary>
public sealed class SystemConfigSingletonTests
{
    [Fact]
    public async Task Create_with_IsSingleton_true_succeeds()
    {
        await using var db = DbContextFactory.CreateSqlite();

        db.SystemConfigs.Add(new SystemConfigEntity
        {
            Id = SystemConfigEntity.SingletonId,
            IsSingleton = SystemConfigEntity.SingletonMarker,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.SystemConfigs.SingleAsync();
        Assert.True(reloaded.IsSingleton);
        Assert.Equal(SystemConfigEntity.SingletonId, reloaded.Id);
    }

    /// <summary>
    /// PG-only: a 2nd row with <c>IsSingleton = true</c> is rejected by the
    /// partial unique index <c>ux_systemconfig_singleton</c> (EF raises
    /// <see cref="DbUpdateException"/> wrapping Postgres error 23505
    /// unique_violation). SQLite ignores the <c>HasFilter</c> on
    /// <c>HasIndex</c>, so the invariant cannot be exercised against the
    /// in-memory provider — it is covered by
    /// <c>PostgresSchemaTests.Systemconfig_has_unique_singleton</c>.
    /// </summary>
    [Fact(Skip = "PG-only: SQLite ignores HasFilter on HasIndex. Covered by PostgresSchemaTests.")]
    public async Task Duplicate_IsSingleton_true_fails_on_unique_index()
    {
        // 1st insert with IsSingleton=true succeeds; the 2nd insert would
        // raise DbUpdateException wrapping 23505 on PostgreSQL.
        await Task.CompletedTask;
    }
}
