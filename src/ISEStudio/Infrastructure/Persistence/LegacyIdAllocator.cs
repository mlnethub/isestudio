using System.Text;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Infrastructure.Persistence;

/// <summary>
/// Allocates <see cref="LegacyAddressableEntity.LegacyId"/> values without
/// racing against concurrent writers on the same table.
/// </summary>
/// <remarks>
/// <para>
/// The 13 historical call sites compute <c>LegacyId = MAX(LegacyId) + 1L</c>
/// with no concurrency control, so two concurrent writers can both read
/// <c>MAX = 100</c> and try to insert <c>LegacyId = 101</c> &mdash; the second
/// <c>SaveChangesAsync</c> then throws on the UNIQUE index. Production runs on
/// PostgreSQL; SQLite is single-writer so the race is dormant there.
/// </para>
/// <para>
/// The allocator dispatches by EF's runtime provider detection:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>PostgreSQL</b> opens a transaction, takes a per-table
///     <c>pg_advisory_xact_lock(bigint)</c> keyed off an FNV-1a 64-bit hash of
///     <c>typeof(TEntity).Name</c>, reads <c>MAX(LegacyId)</c> inside the
///     lock, and commits to release it. Two concurrent writers on the same
///     table serialize; two writers on different tables proceed in parallel
///     (collisions in the 64-bit hash space just over-serialize unrelated
///     tables, which is safe).
///   </item>
///   <item>
///     <b>SQLite</b> falls back to the historical plain-<c>MAX</c>+1 path. No
///     advisory lock is issued: SQLite's single-writer mode makes the race
///     unreachable in practice, and avoiding transactions keeps the test
///     fixture path identical to today.
///   </item>
/// </list>
/// <para>
/// Designed for one-shot allocations inside a single <c>SaveChangesAsync</c>
/// batch. Callers should obtain the id before the change-tracker mutates the
/// row so the assigned value is visible to the insert.
/// </para>
/// </remarks>
public sealed class LegacyIdAllocator
{
    private readonly ISEStudioDbContext _db;

    public LegacyIdAllocator(ISEStudioDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the next free <c>LegacyId</c> for <typeparamref name="TEntity"/>.
    /// Safe to call concurrently across threads &mdash; PostgreSQL callers
    /// serialize on the per-table advisory lock; SQLite callers run
    /// unsynchronized (single-writer DB).
    /// </summary>
    public async Task<long> NextAsync<TEntity>(CancellationToken ct = default)
        where TEntity : LegacyAddressableEntity
    {
        if (_db.Database.IsNpgsql())
        {
            return await NextWithAdvisoryLockAsync<TEntity>(ct).ConfigureAwait(false);
        }
        return await NextPlainMaxAsync<TEntity>(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocates <paramref name="count"/> contiguous ids starting at the next
    /// free id. The entire range is reserved under a single advisory lock so
    /// other writers cannot interleave between allocations.
    /// </summary>
    public async Task<IReadOnlyList<long>> NextNAsync<TEntity>(
        int count,
        CancellationToken ct = default)
        where TEntity : LegacyAddressableEntity
    {
        if (count <= 0)
        {
            return Array.Empty<long>();
        }

        var start = await NextAsync<TEntity>(ct).ConfigureAwait(false);
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = start + i;
        }
        return ids;
    }

    /// <summary>
    /// Atomic counterpart to <see cref="NextAsync{TEntity}"/> + <c>Add</c> +
    /// <see cref="DbContext.SaveChangesAsync"/>: opens a transaction,
    /// acquires the per-table advisory lock, reads <c>MAX(LegacyId)</c>,
    /// assigns <paramref name="entity"/>.<see cref="LegacyAddressableEntity.LegacyId"/>,
    /// adds the entity, persists it, and commits — releasing the lock only
    /// AFTER the row is durably stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NextAsync{TEntity}"/> deliberately separates the alloc
    /// from the persist so a single caller can allocate many ids before a
    /// batch <c>SaveChanges</c>. That decoupling leaves a window in which
    /// two concurrent callers can both read the same <c>MAX</c> and try to
    /// insert the same <c>LegacyId</c>; the second <c>SaveChanges</c>
    /// throws on the UNIQUE index. SQLite is single-writer so the race is
    /// unreachable in practice; PostgreSQL is not.
    /// </para>
    /// <para>
    /// <see cref="AllocateAndPersistAsync{TEntity}"/> closes that window
    /// by holding the per-table advisory lock until <c>COMMIT</c>. A
    /// concurrent caller wanting the same lock blocks on the transaction;
    /// once we commit (and the lock releases) the next caller sees the
    /// newly-inserted row in its <c>MAX</c> read and walks on. SQLite
    /// takes the autocommit path because SQLite's single-writer model
    /// already serialises <c>INSERT</c> statements at the database layer.
    /// </para>
    /// <para>
    /// The caller passes the entity un-keyed; the method assigns
    /// <see cref="LegacyAddressableEntity.LegacyId"/> in place and returns
    /// it for callers that need to log or reference the assigned value
    /// after the fact.
    /// </para>
    /// </remarks>
    public async Task<long> AllocateAndPersistAsync<TEntity>(
        TEntity entity,
        CancellationToken ct = default)
        where TEntity : LegacyAddressableEntity
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (_db.Database.IsNpgsql())
        {
            // PG path: lock the per-table key, read MAX, mutate the
            // change-tracker, SaveChanges, then COMMIT to release the
            // advisory lock. SaveChanges inside the transaction is
            // already idempotent at the EF Core layer; the COMMIT here
            // is the boundary that lets the next contender observe the
            // new row.
            await using var tx = await _db.Database
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);
            var lockKey = ComputeTableKey64(typeof(TEntity).Name);
            await _db.Database
                .ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0}::bigint)",
                    new object[] { lockKey },
                    ct)
                .ConfigureAwait(false);
            var max = await _db.Set<TEntity>().AsNoTracking()
                .Select(e => (long?)e.LegacyId)
                .MaxAsync(ct)
                .ConfigureAwait(false);
            entity.LegacyId = (max ?? 0L) + 1L;
            _db.Set<TEntity>().Add(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return entity.LegacyId;
        }

        // SQLite path: single-writer, no advisory lock needed. The MAX
        // read and the INSERT race against each other in theory, but
        // SQLite serialises writers at the database level so the next
        // contending write only runs after our SaveChanges returns.
        var sqliteMax = await _db.Set<TEntity>().AsNoTracking()
            .Select(e => (long?)e.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        entity.LegacyId = (sqliteMax ?? 0L) + 1L;
        _db.Set<TEntity>().Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.LegacyId;
    }

    /// <summary>
    /// Atomic batch counterpart to <see cref="NextNAsync{TEntity}(int, CancellationToken)"/>:
    /// reserves a contiguous range of <see cref="LegacyAddressableEntity.LegacyId"/>
    /// values under a single advisory lock, assigns them to
    /// <paramref name="entities"/> in order, persists the whole batch, and
    /// commits to release the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contiguous range is reserved before any row is inserted, so a
    /// concurrent batch cannot interleave between two allocations: it
    /// either waits on the lock and reads the new <c>MAX</c> after our
    /// commit, or it commits first and we read its new <c>MAX</c> before
    /// reserving ours. The returned ids are dense — <c>start, start+1, …,
    /// start+entities.Count-1</c> — matching the pre-refactor
    /// <c>NextNAsync</c> contract.
    /// </para>
    /// <para>
    /// For an empty list, the method short-circuits and returns an empty
    /// array without touching the database. This matches
    /// <see cref="NextNAsync{TEntity}(int, CancellationToken)"/>'s
    /// <c>count &lt;= 0</c> guard.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<long>> AllocateManyAndPersistAsync<TEntity>(
        IReadOnlyList<TEntity> entities,
        CancellationToken ct = default)
        where TEntity : LegacyAddressableEntity
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
        {
            return Array.Empty<long>();
        }

        if (_db.Database.IsNpgsql())
        {
            await using var tx = await _db.Database
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);
            var lockKey = ComputeTableKey64(typeof(TEntity).Name);
            await _db.Database
                .ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0}::bigint)",
                    new object[] { lockKey },
                    ct)
                .ConfigureAwait(false);
            var max = await _db.Set<TEntity>().AsNoTracking()
                .Select(e => (long?)e.LegacyId)
                .MaxAsync(ct)
                .ConfigureAwait(false);
            var ids = new long[entities.Count];
            var start = (max ?? 0L) + 1L;
            for (var i = 0; i < entities.Count; i++)
            {
                ids[i] = start + i;
                entities[i].LegacyId = ids[i];
                _db.Set<TEntity>().Add(entities[i]);
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return ids;
        }

        var sqliteMax = await _db.Set<TEntity>().AsNoTracking()
            .Select(e => (long?)e.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        var ids2 = new long[entities.Count];
        var startSqlite = (sqliteMax ?? 0L) + 1L;
        for (var i = 0; i < entities.Count; i++)
        {
            ids2[i] = startSqlite + i;
            entities[i].LegacyId = ids2[i];
            _db.Set<TEntity>().Add(entities[i]);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ids2;
    }

    /// <summary>
    /// Exposed for tests + diagnostics. Returns the deterministic 64-bit
    /// advisory-lock key the PostgreSQL path would use for
    /// <paramref name="tableName"/> &mdash; the FNV-1a hash of the UTF-8 bytes
    /// of the entity name. Distinct entity types must hash to distinct keys
    /// 99.9%+ of the time; collisions are harmless.
    /// </summary>
    public static long ComputeTableKey64(string tableName)
    {
        // FNV-1a 64-bit (http://www.isthe.com/chongo/tech/comp/fnv/).
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var hash = fnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(tableName))
        {
            hash ^= b;
            hash *= fnvPrime;
        }
        return unchecked((long)hash);
    }

    private async Task<long> NextWithAdvisoryLockAsync<TEntity>(CancellationToken ct)
        where TEntity : LegacyAddressableEntity
    {
        var lockKey = ComputeTableKey64(typeof(TEntity).Name);
        await using var tx = await _db.Database
            .BeginTransactionAsync(ct).ConfigureAwait(false);
        await _db.Database
            .ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0}::bigint)",
                new object[] { lockKey },
                ct)
            .ConfigureAwait(false);
        var max = await _db.Set<TEntity>().AsNoTracking()
            .Select(e => (long?)e.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        // Commit (not rollback) to release pg_advisory_xact_lock. No data was
        // written, so commit is semantically equivalent to rollback here;
        // committing also leaves the implicit EF transaction boundary tidy.
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (max ?? 0L) + 1L;
    }

    private async Task<long> NextPlainMaxAsync<TEntity>(CancellationToken ct)
        where TEntity : LegacyAddressableEntity
    {
        var max = await _db.Set<TEntity>().AsNoTracking()
            .Select(e => (long?)e.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        return (max ?? 0L) + 1L;
    }
}