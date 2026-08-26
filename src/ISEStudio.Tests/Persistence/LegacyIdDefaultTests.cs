using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Verifies Phase 2 behavior: new rows default legacy_id to 0 (DB DEFAULT 0).
/// LegacyIdAllocator retired; allocator-related tests moved here.
/// </summary>
public sealed class LegacyIdDefaultTests
{
    [Fact]
    public async Task NewRow_LegacyIdIsZero_WhenNotExplicitlySet()
    {
        using var db = DbContextFactory.CreateSqlite();
        var user = new UserEntity
        {
            Username = $"u-{Guid.NewGuid():N}",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(0L, user.LegacyId);

        // Prove the DB stored 0 (DEFAULT 0 on the column), not just the CLR default:
        // detach and re-materialize the row.
        db.ChangeTracker.Clear();
        var reloaded = await db.Users.SingleAsync(u => u.Username == user.Username);
        Assert.Equal(0L, reloaded.LegacyId);
    }

    [Fact]
    public async Task MultipleNewRows_AllHaveLegacyIdZero()
    {
        using var db = DbContextFactory.CreateSqlite();
        db.Users.AddRange(
            new UserEntity { Username = "u1", PasswordHash = "x", IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow },
            new UserEntity { Username = "u2", PasswordHash = "x", IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow }
        );
        await db.SaveChangesAsync();

        // Prove the DB stored 0 for every row, not just the CLR default.
        db.ChangeTracker.Clear();
        Assert.Equal(0L, (await db.Users.SingleAsync(u => u.Username == "u1")).LegacyId);
        Assert.Equal(0L, (await db.Users.SingleAsync(u => u.Username == "u2")).LegacyId);
    }

    [Fact]
    public async Task ExistingRow_LegacyIdUnchanged_OnUpdate()
    {
        using var db = DbContextFactory.CreateSqlite();
        // Seed a row with explicit non-zero LegacyId (simulating historical data)
        var user = new UserEntity
        {
            Username = "u-hist",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LegacyId = 42L,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Detach + re-query to get a fresh materialization
        db.ChangeTracker.Clear();

        var reloaded = await db.Users.SingleAsync(u => u.Username == "u-hist");
        Assert.Equal(42L, reloaded.LegacyId);

        // Update unrelated field
        reloaded.Active = false;
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded2 = await db.Users.SingleAsync(u => u.Username == "u-hist");
        Assert.Equal(42L, reloaded2.LegacyId);
        Assert.False(reloaded2.Active);
    }

    [Fact]
    public async Task ExplicitLegacyId_HonoredWhenSetBeforeAdd()
    {
        using var db = DbContextFactory.CreateSqlite();
        var user = new UserEntity
        {
            Username = "u-explicit",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
            // The setter is public; EF sends non-default LegacyId in the INSERT.
            LegacyId = 999L,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(999L, user.LegacyId);

        // Prove the DB stored the explicit value.
        db.ChangeTracker.Clear();
        var reloaded = await db.Users.SingleAsync(u => u.Username == "u-explicit");
        Assert.Equal(999L, reloaded.LegacyId);
    }
}
