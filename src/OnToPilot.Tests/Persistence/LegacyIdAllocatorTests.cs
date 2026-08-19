using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Tests.Persistence;

/// <summary>
/// Tests for <see cref="LegacyIdAllocator"/> against the SQLite path
/// (the runtime provider configured by <see cref="DbContextFactory"/>).
/// The PostgreSQL <c>pg_advisory_xact_lock</c> branch requires a real
/// Postgres connection and is covered by integration tests outside this
/// slice.
/// </summary>
public sealed class LegacyIdAllocatorTests
{
    [Fact]
    public async Task Sqlite_path_allocates_monotonic_ids()
    {
        await using var db = DbContextFactory.CreateSqlite();
        var allocator = new LegacyIdAllocator(db);

        // The allocator does NOT persist — it just hands back an id and the
        // caller commits via SaveChanges. We use UserEntity because it has
        // no required FK references, so a minimal insert survives without
        // seeding a parent knowledge system first. (AuditEventEntity has a
        // non-null KnowledgeSystemId FK that fails on SQLite when the parent
        // row is missing.)
        var first = await allocator.NextAsync<UserEntity>();
        db.Users.Add(new UserEntity
        {
            LegacyId = first,
            Username = $"u-{first}",
            DisplayName = "u",
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var second = await allocator.NextAsync<UserEntity>();
        db.Users.Add(new UserEntity
        {
            LegacyId = second,
            Username = $"u-{second}",
            DisplayName = "u",
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var third = await allocator.NextAsync<UserEntity>();

        Assert.Equal(first + 1, second);
        Assert.Equal(second + 1, third);
    }

    [Fact]
    public async Task Sqlite_path_different_entity_types_have_independent_sequences()
    {
        await using var db = DbContextFactory.CreateSqlite();
        var allocator = new LegacyIdAllocator(db);

        // Two tables share no allocator state. Seed a UserEntity with
        // LegacyId=42 so the next UserEntity id is 43, while a different
        // entity type (SystemConfigEntity) starts at 1.
        db.Users.Add(new UserEntity
        {
            LegacyId = 42,
            Username = "u-42",
            DisplayName = "u",
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var userNext = await allocator.NextAsync<UserEntity>();
        var configNext = await allocator.NextAsync<SystemConfigEntity>();

        // users.MAX == 42 so next is 43; systemconfig is empty so next is 1.
        Assert.Equal(43L, userNext);
        Assert.Equal(1L, configNext);
    }

    [Fact]
    public async Task Sqlite_path_next_n_returns_contiguous_range()
    {
        await using var db = DbContextFactory.CreateSqlite();
        var allocator = new LegacyIdAllocator(db);

        var start = await allocator.NextAsync<DocumentEntity>();
        var batch = await allocator.NextNAsync<DocumentEntity>(3);

        // Empty DB → start == 1, batch is [1, 2, 3].
        Assert.Equal(1L, start);
        Assert.Equal(new long[] { 1L, 2L, 3L }, batch);
    }

    [Fact]
    public async Task Sqlite_path_next_n_with_zero_or_negative_returns_empty()
    {
        await using var db = DbContextFactory.CreateSqlite();
        var allocator = new LegacyIdAllocator(db);

        Assert.Empty(await allocator.NextNAsync<AuditEventEntity>(0));
        Assert.Empty(await allocator.NextNAsync<AuditEventEntity>(-5));
    }

    [Fact]
    public async Task Sqlite_path_returns_ids_above_any_existing_rows()
    {
        await using var db = DbContextFactory.CreateSqlite();
        // Seed three users with explicit legacy ids; the next allocator
        // call must return one above the max.
        db.Users.Add(new UserEntity { LegacyId = 100, Username = "u-100", DisplayName = "u", PasswordHash = "x", IsAdmin = false, Active = true, CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new UserEntity { LegacyId = 50, Username = "u-50", DisplayName = "u", PasswordHash = "x", IsAdmin = false, Active = true, CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new UserEntity { LegacyId = 200, Username = "u-200", DisplayName = "u", PasswordHash = "x", IsAdmin = false, Active = true, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var allocator = new LegacyIdAllocator(db);
        var next = await allocator.NextAsync<UserEntity>();

        Assert.Equal(201L, next);
    }

    [Fact]
    public void Compute_table_key_64_returns_distinct_keys_for_distinct_names()
    {
        // The eight tables whose LegacyId columns we allocate today. Each
        // must hash to a distinct 64-bit key (collisions only cause minor
        // over-serialization, but a regression that produces identical keys
        // for unrelated tables is a signal the FNV-1a implementation
        // broke). Pairwise distinctness is the contract.
        var names = new[]
        {
            nameof(AuthSessionEntity),
            nameof(AuditEventEntity),
            nameof(DocumentEntity),
            nameof(ChunkEntity),
            nameof(KnowledgeSystemEntity),
            nameof(TermProposalEntity),
            nameof(AboxProvenanceEntity),
            nameof(ValidationDecisionEntity),
        };
        var keys = names.Select(LegacyIdAllocator.ComputeTableKey64).ToList();

        Assert.Equal(names.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Compute_table_key_64_is_deterministic()
    {
        // Same input must always produce the same key (process-stable;
        // cross-process compatibility lets two API pods allocate from the
        // same sequence range).
        var first = LegacyIdAllocator.ComputeTableKey64(nameof(AuditEventEntity));
        var second = LegacyIdAllocator.ComputeTableKey64(nameof(AuditEventEntity));

        Assert.Equal(first, second);
    }
}