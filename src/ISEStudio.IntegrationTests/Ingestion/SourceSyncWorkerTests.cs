using System.Runtime.CompilerServices;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Authorization;
using ISEStudio.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ISEStudio.IntegrationTests.Ingestion;

[Collection(SourceSyncTestCollection.Name)]
public sealed class SourceSyncWorkerTests : IClassFixture<PostgresGraphFixture>, IAsyncLifetime
{
    private readonly PostgresGraphFixture _fixture;

    public SourceSyncWorkerTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        await db.SourceSyncJobs
            .Where(job => job.Status == "queued" || job.Status == "running")
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "failed")
                .SetProperty(job => job.ActiveRunId, (Guid?)null)
                .SetProperty(job => job.LeaseUntil, (DateTimeOffset?)null));
        await db.SourceSyncRuns
            .Where(run => run.Status == "running")
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, "failed")
                .SetProperty(run => run.FinishedAt, DateTimeOffset.UtcNow));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Enqueue_reuses_active_job_and_claim_starts_a_run()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"source-sync-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        var store = new SourceSyncJobStore(contexts, TimeProvider.System);

        var firstJobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var repeatedJobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var claimed = await store.ClaimNextAsync(CancellationToken.None);

        Assert.Equal(firstJobId, repeatedJobId);
        Assert.NotNull(claimed);
        Assert.Equal(firstJobId, claimed!.Id);
        Assert.Equal("running", claimed.Status);
        Assert.NotNull(claimed.ActiveRunId);

        var persistedRun = await db.SourceSyncRuns.AsNoTracking()
            .SingleAsync(run => run.Id == claimed.ActiveRunId);
        Assert.Equal(source.Id, persistedRun.SourceId);
        Assert.Equal("running", persistedRun.Status);
        var persistedSource = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal("running", persistedSource.LastSyncStatus);
    }

    [Fact]
    public async Task Concurrent_claims_return_one_job_for_a_source()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = await CreateSourceAsync(seedDb);
        var seedFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        await new SourceSyncJobStore(seedFactory, TimeProvider.System)
            .EnqueueAsync(source.Id, CancellationToken.None);

        await using var services1 = _fixture.BuildServices();
        await using var services2 = _fixture.BuildServices();
        await using var scope1 = services1.CreateAsyncScope();
        await using var scope2 = services2.CreateAsyncScope();
        var store1 = new SourceSyncJobStore(
            scope1.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(),
            TimeProvider.System);
        var store2 = new SourceSyncJobStore(
            scope2.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(),
            TimeProvider.System);

        var claims = await Task.WhenAll(
            store1.ClaimNextAsync(CancellationToken.None),
            store2.ClaimNextAsync(CancellationToken.None));

        Assert.Single(claims, claim => claim is not null);
        await using var verifyServices = _fixture.BuildServices();
        await using var verifyScope = verifyServices.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        Assert.Equal(1, await verifyDb.SourceSyncRuns.CountAsync(run => run.SourceId == source.Id));
    }

    [Fact]
    public async Task Different_sources_can_be_claimed_concurrently()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var firstSource = await CreateSourceAsync(seedDb);
        var secondSource = await CreateSourceAsync(seedDb);
        var seedFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var enqueueStore = new SourceSyncJobStore(seedFactory, TimeProvider.System);
        await enqueueStore.EnqueueAsync(firstSource.Id, CancellationToken.None);
        await enqueueStore.EnqueueAsync(secondSource.Id, CancellationToken.None);

        await using var services1 = _fixture.BuildServices();
        await using var services2 = _fixture.BuildServices();
        await using var scope1 = services1.CreateAsyncScope();
        await using var scope2 = services2.CreateAsyncScope();
        var store1 = new SourceSyncJobStore(
            scope1.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(),
            TimeProvider.System);
        var store2 = new SourceSyncJobStore(
            scope2.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(),
            TimeProvider.System);

        var claims = await Task.WhenAll(
            store1.ClaimNextAsync(CancellationToken.None),
            store2.ClaimNextAsync(CancellationToken.None));

        Assert.Equal(
            new HashSet<Guid> { firstSource.Id, secondSource.Id },
            claims.Select(claim => Assert.IsType<SourceSyncJobEntity>(claim).SourceId).ToHashSet());
    }

    [Fact]
    public async Task Interval_scheduler_persists_due_time_and_does_not_repeat_after_restart()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var source = await CreateSourceAsync(db);
        source.CreatedAt = now.AddMinutes(-16);
        source.SyncIntervalMinutes = 15;
        await db.SaveChangesAsync();
        var registry = new SourceAdapterRegistry([
            new SourceKindDescriptor(SourceKind.Url, ActiveSync: true, Array.Empty<SourceConfigField>()),
        ]);

        var firstScheduler = new SourceSyncScheduler(contexts, registry, clock);
        Assert.Equal(1, await firstScheduler.ScheduleDueSourcesAsync(CancellationToken.None));
        db.ChangeTracker.Clear();
        var persistedSource = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal(now.AddMinutes(-1), persistedSource.LastScheduledAt);

        var store = new SourceSyncJobStore(contexts, clock);
        var claimed = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        await store.CompleteAsync(claimed.Id, Assert.IsType<Guid>(claimed.ActiveRunId),
            new SourceSyncResult(0, 0, [], IsComplete: true), CancellationToken.None);

        var restartedScheduler = new SourceSyncScheduler(contexts, registry, clock);
        Assert.Equal(0, await restartedScheduler.ScheduleDueSourcesAsync(CancellationToken.None));
        Assert.Single(await db.SourceSyncJobs.Where(job => job.SourceId == source.Id).ToListAsync());
    }

    [Fact]
    public async Task Cron_scheduler_uses_the_next_five_field_occurrence_in_utc()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 9, 29, 8, 59, 30, TimeSpan.Zero));
        var source = await CreateSourceAsync(db);
        source.CreatedAt = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        source.SyncCron = "0 9 * * 1-5";
        await db.SaveChangesAsync();
        var registry = new SourceAdapterRegistry([
            new SourceKindDescriptor(SourceKind.Url, ActiveSync: true, Array.Empty<SourceConfigField>()),
        ]);
        var scheduler = new SourceSyncScheduler(contexts, registry, clock);

        Assert.Equal(0, await scheduler.ScheduleDueSourcesAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await scheduler.ScheduleDueSourcesAsync(CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero),
            (await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id)).LastScheduledAt);
    }

    [Fact]
    public async Task Worker_claims_runs_and_completes_a_source_job()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<ISourceAdapter, EmptySourceAdapter>();
            collection.AddSingleton<SourceSyncJobStore>();
            collection.AddScoped<SourceSyncCoordinator>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = await CreateSourceAsync(db);
        var jobs = scope.ServiceProvider.GetRequiredService<SourceSyncJobStore>();
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var worker = new SourceSyncWorker(
            services.GetRequiredService<IServiceScopeFactory>(), jobs, clock,
            NullLogger<SourceSyncWorker>.Instance,
            Options.Create(new SourceSyncWorkerOptions()));

        Assert.True(await worker.RunNextAsync(CancellationToken.None));

        db.ChangeTracker.Clear();
        var job = await db.SourceSyncJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal("ok", job.Status);
        Assert.Null(job.ActiveRunId);
        var run = await db.SourceSyncRuns.AsNoTracking().SingleAsync(item => item.SourceId == source.Id);
        Assert.Equal("ok", run.Status);
        var persistedSource = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal("ok", persistedSource.LastSyncStatus);
    }

    [Fact]
    public async Task Worker_drain_processes_all_queued_jobs_and_returns_when_empty()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<ISourceAdapter, EmptySourceAdapter>();
            collection.AddSingleton<SourceSyncJobStore>();
            collection.AddScoped<SourceSyncCoordinator>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var firstSource = await CreateSourceAsync(db);
        var secondSource = await CreateSourceAsync(db);
        var jobs = scope.ServiceProvider.GetRequiredService<SourceSyncJobStore>();
        var firstJobId = await jobs.EnqueueAsync(firstSource.Id, CancellationToken.None);
        var secondJobId = await jobs.EnqueueAsync(secondSource.Id, CancellationToken.None);
        var worker = new SourceSyncWorker(
            services.GetRequiredService<IServiceScopeFactory>(), jobs, clock,
            NullLogger<SourceSyncWorker>.Instance,
            Options.Create(new SourceSyncWorkerOptions { MaxConcurrency = 2 }));

        Assert.Equal(2, await worker.DrainQueuedAsync(CancellationToken.None));
        Assert.Equal(0, await worker.DrainQueuedAsync(CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Equal("ok", (await db.SourceSyncJobs.AsNoTracking().SingleAsync(job => job.Id == firstJobId)).Status);
        Assert.Equal("ok", (await db.SourceSyncJobs.AsNoTracking().SingleAsync(job => job.Id == secondJobId)).Status);
    }

    [Fact]
    public async Task Worker_disposes_renewal_timer_when_execution_finishes()
    {
        var clock = new TrackingTimeProvider();
        var adapter = new BlockingSourceAdapter();
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<ISourceAdapter>(adapter);
            collection.AddSingleton<SourceSyncJobStore>();
            collection.AddScoped<SourceSyncCoordinator>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = await CreateSourceAsync(db);
        var jobs = scope.ServiceProvider.GetRequiredService<SourceSyncJobStore>();
        await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var worker = new SourceSyncWorker(
            services.GetRequiredService<IServiceScopeFactory>(), jobs, clock,
            NullLogger<SourceSyncWorker>.Instance,
            Options.Create(new SourceSyncWorkerOptions { LeaseRenewInterval = TimeSpan.FromHours(1) }));

        var execution = worker.RunNextAsync(CancellationToken.None);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        adapter.CompleteDiscovery();
        await execution;

        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task Worker_cancellation_marks_the_run_failed_and_releases_renewal_timer()
    {
        var clock = new TrackingTimeProvider();
        var adapter = new BlockingSourceAdapter();
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<ISourceAdapter>(adapter);
            collection.AddSingleton<SourceSyncJobStore>();
            collection.AddScoped<SourceSyncCoordinator>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = await CreateSourceAsync(db);
        var jobs = scope.ServiceProvider.GetRequiredService<SourceSyncJobStore>();
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        using var stopping = new CancellationTokenSource();
        var worker = new SourceSyncWorker(
            services.GetRequiredService<IServiceScopeFactory>(), jobs, clock,
            NullLogger<SourceSyncWorker>.Instance,
            Options.Create(new SourceSyncWorkerOptions { LeaseRenewInterval = TimeSpan.FromHours(1) }));

        var execution = worker.RunNextAsync(stopping.Token);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopping.Cancel();
        await execution;

        Assert.Equal(0, clock.ActiveTimerCount);
        db.ChangeTracker.Clear();
        var job = await db.SourceSyncJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        var run = await db.SourceSyncRuns.AsNoTracking().SingleAsync(item => item.SourceId == source.Id);
        Assert.Equal("failed", job.Status);
        Assert.Equal("failed", run.Status);
    }

    [Fact]
    public async Task In_transaction_renewal_survives_lease_expiry_while_source_lock_is_held()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = await CreateSourceAsync(db);
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new SourceSyncJobStore(contexts, clock, TimeSpan.FromSeconds(30));
        var jobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var claimed = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        var runId = Assert.IsType<Guid>(claimed.ActiveRunId);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await store.ValidateClaimUnderLockAsync(
                db, source.Id, jobId, runId, CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True(await store.RenewUnderLockAsync(
                db, source.Id, jobId, runId, CancellationToken.None));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await store.RecoverExpiredAsync(CancellationToken.None));
        var persistedJob = await db.SourceSyncJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        Assert.Equal(runId, persistedJob.ActiveRunId);
        var expectedLease = clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        Assert.InRange(persistedJob.LeaseUntil!.Value, expectedLease.AddTicks(-10), expectedLease.AddTicks(10));
    }

    [Fact]
    public async Task Concurrent_enqueue_and_delete_leave_one_serialized_outcome()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var (source, actor) = await CreateSourceAndActorAsync(seedDb);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueueTask = Task.Run(async () =>
        {
            await start.Task;
            await using var services = _fixture.BuildServices();
            await using var scope = services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
            try
            {
                return (Guid?)await new SourceSyncJobStore(factory, TimeProvider.System)
                    .EnqueueAsync(source.Id, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
        var deleteTask = Task.Run(async () =>
        {
            await start.Task;
            await using var services = _fixture.BuildServices();
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var persistedActor = await db.Users.SingleAsync(user => user.Id == actor.Id);
            var result = await CreateSourceService(db).DeleteAsync(
                _fixture.KnowledgeSystemId, source.Id, persistedActor, CancellationToken.None);
            return result.StatusCode;
        });

        start.SetResult();
        var enqueuedJobId = await enqueueTask;
        var deleteStatus = await deleteTask;

        await using var verifyServices = _fixture.BuildServices();
        await using var verifyScope = verifyServices.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var sourceExists = await verifyDb.Sources.AnyAsync(item => item.Id == source.Id);
        if (sourceExists)
        {
            Assert.Equal(409, deleteStatus);
            Assert.NotNull(enqueuedJobId);
            Assert.True(await verifyDb.SourceSyncJobs.AnyAsync(job =>
                job.Id == enqueuedJobId && job.Status == "queued"));
        }
        else
        {
            Assert.Equal(204, deleteStatus);
            Assert.Null(enqueuedJobId);
            Assert.False(await verifyDb.SourceSyncJobs.AnyAsync(job => job.SourceId == source.Id));
        }
    }

    [Fact]
    public async Task Concurrent_claim_and_delete_are_serialized_by_the_source_lock()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var (source, actor) = await CreateSourceAndActorAsync(seedDb);
        var seedFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seededJobId = await new SourceSyncJobStore(seedFactory, TimeProvider.System)
            .EnqueueAsync(source.Id, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claimTask = Task.Run(async () =>
        {
            await start.Task;
            await using var services = _fixture.BuildServices();
            await using var scope = services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
            return await new SourceSyncJobStore(factory, TimeProvider.System)
                .ClaimNextAsync(CancellationToken.None);
        });
        var deleteTask = Task.Run(async () =>
        {
            await start.Task;
            return await DeleteSourceAsync(source.Id, actor.Id);
        });

        start.SetResult();
        var claimed = await claimTask;
        var deleteStatus = await deleteTask;

        await using var verifyServices = _fixture.BuildServices();
        await using var verifyScope = verifyServices.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var sourceExists = await verifyDb.Sources.AnyAsync(item => item.Id == source.Id);
        if (sourceExists)
        {
            Assert.Equal(409, deleteStatus);
            Assert.NotNull(claimed);
            Assert.Equal(seededJobId, claimed!.Id);
            Assert.Equal("running", claimed.Status);
            Assert.True(await verifyDb.SourceSyncRuns.AnyAsync(run =>
                run.SourceId == source.Id && run.Id == claimed.ActiveRunId));
        }
        else
        {
            Assert.Equal(204, deleteStatus);
            Assert.Null(claimed);
            Assert.False(await verifyDb.SourceSyncJobs.AnyAsync(job => job.SourceId == source.Id));
            Assert.False(await verifyDb.SourceSyncRuns.AnyAsync(run => run.SourceId == source.Id));
        }
    }

    [Fact]
    public async Task Running_job_blocks_delete_but_terminal_job_is_cascaded_after_delete()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var (source, actor) = await CreateSourceAndActorAsync(seedDb);
        var factory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new SourceSyncJobStore(factory, TimeProvider.System);
        var jobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var claimed = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        var runId = Assert.IsType<Guid>(claimed.ActiveRunId);

        var blockedDelete = await DeleteSourceAsync(source.Id, actor.Id);
        Assert.Equal(409, blockedDelete);
        await store.CompleteAsync(jobId, runId,
            new SourceSyncResult(0, 0, [], IsComplete: true), CancellationToken.None);

        Assert.Equal(204, await DeleteSourceAsync(source.Id, actor.Id));
        seedDb.ChangeTracker.Clear();
        Assert.False(await seedDb.Sources.AnyAsync(item => item.Id == source.Id));
        Assert.False(await seedDb.SourceSyncJobs.AnyAsync(job => job.SourceId == source.Id));
        Assert.False(await seedDb.SourceSyncRuns.AnyAsync(run => run.SourceId == source.Id));
    }

    [Fact]
    public async Task Failed_completion_records_result_and_prunes_history_to_fifty_runs()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = await CreateSourceAsync(db);
        var now = DateTimeOffset.UtcNow;
        db.SourceSyncRuns.AddRange(Enumerable.Range(1, 50).Select(index => new SourceSyncRunEntity
        {
            SourceId = source.Id,
            Status = "failed",
            StartedAt = now.AddMinutes(-index),
            FinishedAt = now.AddMinutes(-index).AddSeconds(1),
            Error = "older run",
        }));
        await db.SaveChangesAsync();

        var store = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var claimed = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        var runId = Assert.IsType<Guid>(claimed.ActiveRunId);
        await store.CompleteAsync(jobId, runId,
            new SourceSyncResult(3, 4, ["page fetch failed"], IsComplete: false), CancellationToken.None);

        db.ChangeTracker.Clear();
        var runs = await db.SourceSyncRuns.AsNoTracking()
            .Where(run => run.SourceId == source.Id)
            .OrderByDescending(run => run.StartedAt)
            .ToListAsync();
        Assert.Equal(50, runs.Count);
        var completedRun = Assert.Single(runs, run => run.Id == runId);
        Assert.Equal("failed", completedRun.Status);
        Assert.NotNull(completedRun.FinishedAt);
        Assert.Equal(3, completedRun.AddedCount);
        Assert.Equal(4, completedRun.UpdatedCount);
        Assert.Equal("page fetch failed", completedRun.Error);
        var persistedSource = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal("failed", persistedSource.LastSyncStatus);
        Assert.Equal("page fetch failed", persistedSource.LastSyncError);
        Assert.Equal(3, persistedSource.LastSyncAdded);
    }

    [Fact]
    public async Task Expired_run_is_replaced_and_late_completion_cannot_overwrite_new_run()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = await CreateSourceAsync(db);
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new SourceSyncJobStore(contexts, clock, TimeSpan.FromSeconds(30));
        var jobId = await store.EnqueueAsync(source.Id, CancellationToken.None);
        var oldClaim = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        var oldRunId = Assert.IsType<Guid>(oldClaim.ActiveRunId);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await store.RenewAsync(jobId, oldRunId, CancellationToken.None));
        Assert.Equal(1, await store.RecoverExpiredAsync(CancellationToken.None));
        var newClaim = Assert.IsType<SourceSyncJobEntity>(await store.ClaimNextAsync(CancellationToken.None));
        var newRunId = Assert.IsType<Guid>(newClaim.ActiveRunId);
        Assert.NotEqual(oldRunId, newRunId);

        await store.CompleteAsync(jobId, oldRunId,
            new SourceSyncResult(99, 99, [], IsComplete: true), CancellationToken.None);
        db.ChangeTracker.Clear();
        var sourceAfterLateCompletion = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal("running", sourceAfterLateCompletion.LastSyncStatus);
        Assert.Equal(0, sourceAfterLateCompletion.LastSyncAdded);
        Assert.Equal("failed", (await db.SourceSyncRuns.AsNoTracking().SingleAsync(run => run.Id == oldRunId)).Status);
        Assert.Equal("running", (await db.SourceSyncRuns.AsNoTracking().SingleAsync(run => run.Id == newRunId)).Status);

        await store.CompleteAsync(jobId, newRunId,
            new SourceSyncResult(2, 5, [], IsComplete: true), CancellationToken.None);
        var completedSource = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.Equal("ok", completedSource.LastSyncStatus);
        Assert.Equal(2, completedSource.LastSyncAdded);
        Assert.Equal("ok", (await db.SourceSyncRuns.AsNoTracking().SingleAsync(run => run.Id == newRunId)).Status);
    }

    private async Task<SourceEntity> CreateSourceAsync(ISEStudioDbContext db)
    {
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"source-sync-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<(SourceEntity Source, UserEntity Actor)> CreateSourceAndActorAsync(
        ISEStudioDbContext db)
    {
        var knowledgeSystem = await db.KnowledgeSystems.SingleAsync(
            item => item.Id == _fixture.KnowledgeSystemId);
        var actor = new UserEntity
        {
            Username = $"source-sync-owner-{Guid.NewGuid():N}",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"source-sync-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        knowledgeSystem.OwnerId = actor.Id;
        db.Users.Add(actor);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return (source, actor);
    }

    private async Task<int> DeleteSourceAsync(Guid sourceId, Guid actorId)
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var actor = await db.Users.SingleAsync(user => user.Id == actorId);
        return (await CreateSourceService(db).DeleteAsync(
            _fixture.KnowledgeSystemId, sourceId, actor, CancellationToken.None)).StatusCode;
    }

    private static SourceService CreateSourceService(ISEStudioDbContext db)
        => new(db, new KnowledgeSystemAccessService(), new SourceAdapterRegistry([]),
            new SourceSecretProtector(new ConfigurationBuilder().Build()), TimeProvider.System);

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private int _activeTimerCount;

        public TaskCompletionSource TimerCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveTimerCount => Volatile.Read(ref _activeTimerCount);

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _activeTimerCount);
            TimerCreated.TrySetResult();
            return new TrackingTimer(this);
        }

        private void OnTimerDisposed() => Interlocked.Decrement(ref _activeTimerCount);

        private sealed class TrackingTimer(TrackingTimeProvider owner) : ITimer
        {
            private TrackingTimeProvider? _owner = owner;

            public bool Change(TimeSpan dueTime, TimeSpan period) => _owner is not null;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.OnTimerDisposed();

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BlockingSourceAdapter : ISourceAdapter
    {
        private readonly TaskCompletionSource _discovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => SourceKind.Url;

        public void CompleteDiscovery() => _discovery.TrySetResult();

        public async Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
        {
            await _discovery.Task.WaitAsync(cancellationToken);
            return new SourceScan(Empty(cancellationToken), IsComplete: true);
        }

        private static async IAsyncEnumerable<SourceItem> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class EmptySourceAdapter : ISourceAdapter
    {
        public string Kind => SourceKind.Url;

        public Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceScan(EnumerateEmpty(cancellationToken), IsComplete: true));
        }

        private static async IAsyncEnumerable<SourceItem> EnumerateEmpty(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}