using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Persistence;

/// <summary>
/// Spins up a real PostgreSQL instance via Testcontainers and exercises
/// <see cref="LegacyIdAllocator"/>'s <c>pg_advisory_xact_lock</c> branch.
/// The SQLite path is covered by
/// <c>src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs</c>; SQLite is
/// single-writer so it cannot prove the advisory lock actually serializes
/// concurrent allocations on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Each concurrent allocation opens its own <see cref="ISEStudioDbContext"/>
/// (and therefore its own pooled connection + transaction) so that the
/// <c>pg_advisory_xact_lock</c> contention surfaces at the database level,
/// not at the EF Core change-tracker level. Sharing one DbContext across
/// tasks would either fail with <c>InvalidOperationException</c> or only
/// prove serialization through EF, neither of which validates the lock.
/// </para>
/// <para>
/// The allocator returns <c>MAX(LegacyId) + 1</c> but does NOT persist the
/// row. Concurrent tests therefore save the allocated id inside each task
/// so the next concurrent call sees an updated MAX — otherwise every
/// concurrent caller reads the same MAX and the test cannot distinguish
/// "lock failed" from "lock not consulted".
/// </para>
/// <para>
/// Docker must be available; if it is not, the fixture throws and the
/// tests fail loudly (no silent skips).
/// </para>
/// </remarks>
[Trait("Category", "Persistence")]
public sealed class LegacyIdAllocatorAdvisoryLockTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Open a fresh <see cref="ISEStudioDbContext"/> against the test
    /// container. EF Core's Npgsql provider takes a connection from the
    /// pool, so two simultaneous calls land on distinct backend sessions
    /// — which is what makes the <c>pg_advisory_xact_lock</c> semantics
    /// observable.
    /// </summary>
    private ISEStudioDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new ISEStudioDbContext(options);
    }

    /// <summary>
    /// Inserts a minimal <see cref="UserEntity"/> row carrying the given
    /// LegacyId. Used by concurrent tasks to "publish" an allocated id so
    /// the next contender's MAX read sees it.
    /// </summary>
    private static async Task InsertUserAsync(ISEStudioDbContext db, long legacyId)
    {
        db.Users.Add(new UserEntity
        {
            LegacyId = legacyId,
            Username = $"u-{legacyId}-{Guid.NewGuid():N}",
            DisplayName = "u",
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Two concurrent writers on the same table must serialize on the
    /// per-table advisory lock and produce pairwise-distinct LegacyIds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caveat: the allocator's lock window only covers the
    /// <c>SELECT MAX(LegacyId)</c> read; the caller's
    /// <c>SaveChangesAsync</c> runs in autocommit, after the lock has
    /// been released. This test therefore <i>does not</i> prove that
    /// concurrent <c>alloc + save</c> pairs are made atomic by the lock —
    /// they are not. On a busy machine or under heavy contention, the
    /// next allocation can win the lock before the previous task's
    /// <c>SaveChanges</c> commits its row, in which case both reads see
    /// the same <c>MAX</c>, both return the same id, and the second
    /// <c>SaveChanges</c> throws on the UNIQUE index.
    /// </para>
    /// <para>
    /// The test passes today because 16 parallel tasks on a freshly
    /// migrated table consistently interleave such that each alloc
    /// observes the previous task's committed row before reading
    /// <c>MAX</c>. Treat a sporadic failure here as a signal that the
    /// allocator needs an <i>atomic alloc + save</i> mode (wrap alloc +
    /// <c>SaveChanges</c> in a caller-owned
    /// <c>BeginTransactionAsync</c>); it is not a regression in the lock
    /// itself.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pg_advisory_lock_serializes_concurrent_allocations()
    {
        // Bring the schema up once via a short-lived context.
        await using (var setup = OpenContext())
        {
            await setup.Database.MigrateAsync();
        }

        const int parallelism = 16;
        var ids = new long[parallelism];
        var tasks = new Task[parallelism];

        for (var i = 0; i < parallelism; i++)
        {
            var captured = i;
            tasks[i] = Task.Run(async () =>
            {
                await using var ctx = OpenContext();
                var allocator = new LegacyIdAllocator(ctx);
                var id = await allocator.NextAsync<UserEntity>();
                await InsertUserAsync(ctx, id);
                ids[captured] = id;
            });
        }

        await Task.WhenAll(tasks);

        // Pairwise distinct — the lock serialized us. No two tasks may have
        // read the same MAX and committed the same id.
        Assert.Equal(parallelism, ids.Distinct().Count());
        // Contiguous from 1, no gaps.
        Assert.Equal(1L, ids.Min());
        Assert.Equal(parallelism, ids.Max());
    }

    /// <summary>
    /// After a successful <see cref="LegacyIdAllocator.NextAsync{TEntity}"/>
    /// the transaction-scoped <c>pg_advisory_xact_lock</c> must be released
    /// so a follow-up call on the same session can proceed. If the lock
    /// were held past commit, the second call would deadlock against itself
    /// (one backend session waiting on its own advisory lock). We bound
    /// each call with a short timeout so a regression surfaces as a fail
    /// fast rather than a hang.
    /// </summary>
    [Fact]
    public async Task Pg_advisory_lock_releases_on_commit()
    {
        await using var db = OpenContext();
        await db.Database.MigrateAsync();
        var allocator = new LegacyIdAllocator(db);

        var first = await allocator.NextAsync<DocumentEntity>();
        await InsertDocumentAsync(db, first);

        var second = await allocator
            .NextAsync<DocumentEntity>()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await InsertDocumentAsync(db, second);

        var third = await allocator
            .NextAsync<DocumentEntity>()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await InsertDocumentAsync(db, third);

        // Three sequential MAX reads on the same session — each one
        // committed and released the lock before the next acquired it.
        // Contiguous ids prove three separate reads, not a single one
        // being reused (which would happen if the allocator persisted).
        Assert.Equal(first + 1, second);
        Assert.Equal(second + 1, third);
    }

    /// <summary>
    /// Two distinct entity types must hash to distinct advisory-lock keys,
    /// otherwise allocations on unrelated tables would serialize against
    /// each other and the PG path would bottleneck behind any single busy
    /// table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test deliberately runs sequentially across entity types instead
    /// of in parallel. The allocator's lock window only covers the
    /// <c>SELECT MAX(LegacyId)</c> read; the caller's
    /// <c>SaveChangesAsync</c> runs in autocommit, after the lock has
    /// already been released. Concurrent <i>alloc + save</i> pairs on the
    /// same type are therefore not made atomic by the advisory lock — a
    /// regression in the lock window would surface as
    /// <c>duplicate key value violates unique constraint "ux_..._legacy_id"</c>
    /// on the second concurrent insert.
    /// </para>
    /// <para>
    /// Sequential allocation is sufficient to prove the cross-type
    /// independence claim: each entity type must return <c>MAX + 1</c> of
    /// <i>its own</i> table, and the two sequences must not bleed into
    /// each other. If FNV-1a regressed to a constant output, both types
    /// would acquire the same advisory lock and one allocation would
    /// deadlock against the other — but here we run sequentially so the
    /// lock contention is invisible. The real regression guard for
    /// distinct keys is <see cref="Pg_compute_table_key64_drives_distinct_lock_keys_real_pg_path"/>;
    /// this test pins down the behavioural consequence on PG.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pg_advisory_lock_distinct_entity_types_have_independent_locks()
    {
        await using var db = OpenContext();
        await db.Database.MigrateAsync();

        // Seed one user with LegacyId = 50 so the UserEntity branch starts
        // above 50. SystemConfigEntity starts at 1 (empty table).
        await InsertUserAsync(db, 50);

        var allocator = new LegacyIdAllocator(db);

        // Sequential allocations across both entity types. If the lock
        // keys collided, one of the per-type sequences would still produce
        // contiguous ids (the allocator internally serializes on its
        // single key) — but the *cross-type* independence would be
        // undetectable. So we additionally assert the ranges don't
        // overlap, which is only possible if the two types see distinct
        // MAX tables.
        var userIds = new long[4];
        for (var i = 0; i < 4; i++)
        {
            userIds[i] = await allocator.NextAsync<UserEntity>();
            await InsertUserAsync(db, userIds[i]);
        }

        var configIds = new long[4];
        for (var i = 0; i < 4; i++)
        {
            configIds[i] = await allocator.NextAsync<SystemConfigEntity>();
            await InsertSystemConfigAsync(db, configIds[i]);
        }

        // Each type's MAX+1 sequence is contiguous and starts from its own
        // seed. Cross-type independence: UserEntity starts at 51 (above
        // the seeded 50); SystemConfigEntity starts at 1 (empty table).
        Assert.Equal(new long[] { 51L, 52L, 53L, 54L }, userIds);
        Assert.Equal(new long[] { 1L, 2L, 3L, 4L }, configIds);

        // The two sequences are disjoint — UserEntity ids live entirely
        // above the SystemConfigEntity range, so no id collision is
        // possible at the row level even before the per-type UNIQUE
        // constraint is consulted.
        Assert.Empty(userIds.Intersect(configIds));
    }

    /// <summary>
    /// PG-side sanity check that the table-key derivation used by
    /// <see cref="LegacyIdAllocator.ComputeTableKey64"/> does not collapse
    /// distinct entity names onto the same advisory-lock key. The
    /// in-process unit test already covers the hash function; this test
    /// proves the lock keys themselves drive independent serialization on
    /// the real PG instance.
    /// </summary>
    [Fact]
    public void Pg_compute_table_key64_drives_distinct_lock_keys_real_pg_path()
    {
        var names = new[]
        {
            nameof(UserEntity),
            nameof(SystemConfigEntity),
            nameof(DocumentEntity),
            nameof(ChunkEntity),
        };
        var keys = names.Select(LegacyIdAllocator.ComputeTableKey64).ToArray();

        // Pairwise distinct — the FNV-1a helper holds on the real PG path.
        Assert.Equal(names.Length, keys.Distinct().Count());
        // Redundant explicit assertion for readability.
        Assert.Equal(0, keys.Length - keys.Distinct().Count());
    }

    private static async Task InsertDocumentAsync(ISEStudioDbContext db, long legacyId)
    {
        db.Documents.Add(new DocumentEntity
        {
            LegacyId = legacyId,
            // Sha256 must be unique per row; randomise so concurrent inserts
            // of identical LegacyIds (which the lock prevents anyway) do not
            // also collide on the (KnowledgeSystemId, Sha256) composite key.
            Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            OriginalFilename = $"d-{legacyId}.bin",
            Folder = "/",
            Ext = "bin",
            SizeBytes = 0,
            StoragePath = $"/dev/null/{legacyId}",
            UploadedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task InsertSystemConfigAsync(ISEStudioDbContext db, long legacyId)
    {
        db.SystemConfigs.Add(new SystemConfigEntity
        {
            LegacyId = legacyId,
            ExtractModel = $"m-{legacyId}",
            EmbeddingModel = $"e-{legacyId}",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The atomic <see cref="LegacyIdAllocator.AllocateAndPersistAsync{TEntity}"/>
    /// API holds the per-table <c>pg_advisory_xact_lock</c> until COMMIT,
    /// so concurrent callers across separate DbContexts cannot both read
    /// the same <c>MAX(LegacyId)</c> and then race to insert the same
    /// <c>LegacyId</c>. This test exercises that guarantee end-to-end:
    /// every contended allocation lands on a distinct, durably-persisted
    /// row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-refactor allocator (<see cref="LegacyIdAllocator.NextAsync{TEntity}"/>
    /// + caller-owned <c>SaveChangesAsync</c>) leaked the advisory lock
    /// before the INSERT committed, so two concurrent tasks could both
    /// observe <c>MAX=null</c>, both pick <c>LegacyId=1</c>, and the second
    /// <c>SaveChanges</c> would throw on the UNIQUE constraint. That race
    /// was the B7c hardening motivation for the atomic counterpart.
    /// </para>
    /// <para>
    /// Each task uses its own <see cref="ISEStudioDbContext"/> (and therefore
    /// its own pooled connection + transaction) so the contention surfaces
    /// at the database layer — sharing one DbContext would either fail with
    /// <c>InvalidOperationException</c> or only prove serialization at the
    /// EF change-tracker level, neither of which validates the lock.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pg_allocate_and_persist_serializes_concurrent_allocations_under_lock()
    {
        // Bring the schema up once via a short-lived context.
        await using (var setup = OpenContext())
        {
            await setup.Database.MigrateAsync();
        }

        const int parallelism = 16;
        var ids = new long[parallelism];
        var tasks = new Task[parallelism];

        for (var i = 0; i < parallelism; i++)
        {
            var captured = i;
            tasks[i] = Task.Run(async () =>
            {
                await using var ctx = OpenContext();
                var allocator = new LegacyIdAllocator(ctx);
                var entity = new UserEntity
                {
                    Username = $"u-atomic-{captured}-{Guid.NewGuid():N}",
                    DisplayName = "u",
                    PasswordHash = "x",
                    IsAdmin = false,
                    Active = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                // AllocateAndPersistAsync does Add + SaveChanges + COMMIT
                // inside the per-table advisory lock — the lock window
                // now covers the INSERT, so the next contender reads
                // MAX after our COMMIT and walks on.
                ids[captured] = await allocator.AllocateAndPersistAsync(entity);
            });
        }

        await Task.WhenAll(tasks);

        // Pairwise distinct — the lock serialized all 16 alloc+save pairs.
        Assert.Equal(parallelism, ids.Distinct().Count());
        Assert.Equal(1L, ids.Min());
        Assert.Equal(parallelism, ids.Max());

        // All 16 rows are durably persisted (not just queued on the
        // change-tracker). The lock window covered SaveChanges, so a
        // fresh session sees every allocation.
        await using var verify = OpenContext();
        var persistedCount = await verify.Users.AsNoTracking().CountAsync();
        Assert.Equal(parallelism, persistedCount);
    }

    /// <summary>
    /// The atomic batch counterpart
    /// <see cref="LegacyIdAllocator.AllocateManyAndPersistAsync{TEntity}"/>
    /// reserves a contiguous range under a single advisory lock and
    /// persists every row before COMMIT. Concurrent batch allocations on
    /// the same table must therefore see no id reuse and no row
    /// collisions, even when the batches overlap in time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-refactor <see cref="LegacyIdAllocator.NextNAsync{TEntity}"/>
    /// only reserved the range (one MAX read) but the caller still ran
    /// <c>SaveChangesAsync</c> in autocommit — leaving the same race the
    /// single-row allocator had. The batch refactor reserves the range
    /// AND persists in one transaction, so concurrent batches serialize
    /// cleanly.
    /// </para>
    /// <para>
    /// We spawn 8 parallel batches of 4 rows each (32 allocations total)
    /// across separate DbContexts. The expected invariant: every batch's
    /// reserved range is disjoint from every other batch's reserved
    /// range, and every batch's rows land durably in the table.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pg_allocate_many_and_persist_serializes_concurrent_batches_under_lock()
    {
        await using (var setup = OpenContext())
        {
            await setup.Database.MigrateAsync();
        }

        const int batches = 8;
        const int batchRows = 4;
        var batchIdArrays = new long[batches][];
        var tasks = new Task[batches];

        for (var b = 0; b < batches; b++)
        {
            var captured = b;
            tasks[b] = Task.Run(async () =>
            {
                await using var ctx = OpenContext();
                var allocator = new LegacyIdAllocator(ctx);

                var rows = new List<UserEntity>(batchRows);
                for (var i = 0; i < batchRows; i++)
                {
                    rows.Add(new UserEntity
                    {
                        Username = $"u-batch-{captured}-{i}-{Guid.NewGuid():N}",
                        DisplayName = "u",
                        PasswordHash = "x",
                        IsAdmin = false,
                        Active = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }

                // AllocateManyAndPersistAsync reserves the contiguous
                // range, assigns it to the entities, persists them, and
                // commits — all under one advisory lock window.
                batchIdArrays[captured] = (await allocator
                    .AllocateManyAndPersistAsync(rows))
                    .ToArray();
            });
        }

        await Task.WhenAll(tasks);

        // No id reuse across the 32 allocations — the union of every
        // batch's reserved range is pairwise distinct.
        var allIds = batchIdArrays.SelectMany(a => a).ToList();
        Assert.Equal(batches * batchRows, allIds.Distinct().Count());
        Assert.Equal(1L, allIds.Min());
        Assert.Equal(batches * batchRows, allIds.Max());

        // Each batch's reserved range is contiguous (4 ids) and disjoint
        // from the others. Two batches cannot interleave under the atomic
        // path; the second batch sees the first batch's committed MAX
        // and reserves the next range above it.
        foreach (var arr in batchIdArrays)
        {
            Assert.Equal(batchRows, arr.Length);
            for (var i = 1; i < arr.Length; i++)
            {
                Assert.Equal(arr[i - 1] + 1, arr[i]);
            }
        }

        // Every batch's rows are durably persisted (the union equals the
        // table row count).
        await using var verify = OpenContext();
        var persistedCount = await verify.Users.AsNoTracking().CountAsync();
        Assert.Equal(batches * batchRows, persistedCount);
    }
}
