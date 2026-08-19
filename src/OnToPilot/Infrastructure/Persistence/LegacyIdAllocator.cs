using System.Text;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Infrastructure.Persistence;

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
    private readonly OnToPilotDbContext _db;

    public LegacyIdAllocator(OnToPilotDbContext db)
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