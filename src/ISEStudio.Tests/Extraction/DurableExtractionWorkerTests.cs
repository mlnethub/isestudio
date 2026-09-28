using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Regression tests for the durable extraction worker's failure handling.
/// An unexpected exception in <see cref="DurableExtractionWorker.ExecuteAsync"/>
/// must not escape — <c>BackgroundServiceExceptionBehavior</c> defaults to
/// <c>StopHost</c>, so an escaped exception kills the whole API host and
/// leaves the claimed row stuck in <c>running</c>, which keeps the 409
/// active-job guard locked until the next boot's stale-job recovery.
/// </summary>
public sealed class DurableExtractionWorkerTests
{
    [Fact]
    public void Production_durable_registration_supports_combined_jobs()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddExtractionServices();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DurableExtractionWorkerOptions>>().Value;
        Assert.Contains(ExtractionWire.KindBoth, options.SupportedKinds);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IExtractionJobHandler)
            && descriptor.ImplementationType == typeof(CombinedExtractionJobHandler));
    }

    /// <summary>
    /// Exposes <see cref="BackgroundService.ExecuteAsync"/> so the test can
    /// observe the real loop task — <see cref="BackgroundService.StartAsync"/>
    /// returns <c>Task.CompletedTask</c> while the loop is still running.
    /// </summary>
    private sealed class TestWorker : DurableExtractionWorker
    {
        public TestWorker(
            IServiceScopeFactory scopeFactory,
            ExtractionJobStore jobs,
            TimeProvider clock,
            ILogger<DurableExtractionWorker> logger,
            IOptions<DurableExtractionWorkerOptions>? options = null)
            : base(scopeFactory, jobs, clock, logger, options)
        {
        }

        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private static TestWorker BuildWorker(
        IServiceProvider provider,
        ExtractionJobStore store)
    {
        return new TestWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            provider.GetRequiredService<ILogger<DurableExtractionWorker>>(),
            provider.GetRequiredService<IOptions<DurableExtractionWorkerOptions>>());
    }

    private static async Task<Guid> SeedKnowledgeSystemAsync(
        IDbContextFactory<ISEStudioDbContext> contexts)
    {
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        var ks = db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            PublicId = "kstest" + Guid.NewGuid().ToString("N")[..10],
            Name = "ks-test",
            Description = "",
            OwnerId = null,
            GraphIri = "http://isestudio.test/ks/" + Guid.NewGuid().ToString("N"),
            BaseIri = "http://isestudio.test/ks/" + Guid.NewGuid().ToString("N") + "/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return ks.Entity.Id;
    }

    private static async Task<ExtractionJobEntity> SeedJobAsync(
        ExtractionJobStore store,
        IDbContextFactory<ISEStudioDbContext> contexts,
        string kind = "tbox_extract")
    {
        var ksId = await SeedKnowledgeSystemAsync(contexts);
        return await store.CreateAsync(
            ksId, kind, "test-model",
            Array.Empty<int>(), 0, CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_resolution_failure_marks_job_failed_and_keeps_polling()
    {
        using var contexts = new SqliteContextFactory();
        var store = new ExtractionJobStore(contexts, TimeProvider.System);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.Configure<DurableExtractionWorkerOptions>(o =>
            o.PollInterval = TimeSpan.FromMilliseconds(100));
        using var provider = services.BuildServiceProvider();
        var worker = BuildWorker(provider, store);

        using var cts = new CancellationTokenSource();
        // No ExtractionJobDispatcher registered on purpose: the scope
        // resolution throws exactly like the test-host DI gap that used
        // to stop the host.
        var run = worker.RunAsync(cts.Token);

        var first = await SeedJobAsync(store, contexts);
        var failedFirst = await store.WaitAsync(first.Id, CancellationToken.None);
        Assert.Equal("failed", failedFirst.Status);
        Assert.Contains("InvalidOperationException", failedFirst.Error);

        // The worker survives and keeps polling: a second pending job is
        // also claimed and failed rather than leaving the host dead.
        Assert.False(run.IsCompleted, "worker must keep polling after a failed dispatch");
        var second = await SeedJobAsync(store, contexts);
        var failedSecond = await store.WaitAsync(second.Id, CancellationToken.None);
        Assert.Equal("failed", failedSecond.Status);

        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    [Fact]
    public async Task Shutdown_cancellation_propagates_without_marking_in_flight_job_failed()
    {
        using var contexts = new SqliteContextFactory();
        var store = new ExtractionJobStore(contexts, TimeProvider.System);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<IExtractionJobHandler>(new BlockingHandler());
        services.AddSingleton<ExtractionJobDispatcher>();
        services.Configure<DurableExtractionWorkerOptions>(o =>
            o.PollInterval = TimeSpan.FromMilliseconds(100));
        using var provider = services.BuildServiceProvider();
        var worker = BuildWorker(provider, store);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);

        var job = await SeedJobAsync(store, contexts, BlockingHandler.KindValue);

        // Wait for the worker to claim the job (pending → running).
        ExtractionJobEntity? claimed = null;
        for (var i = 0; i < 100; i++)
        {
            var current = await store.GetAsync(job.Id);
            if (current?.Status == "running")
            {
                claimed = current;
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(claimed);

        // Shutdown mid-dispatch: the OperationCanceledException must
        // propagate (BackgroundService contract) and the failure path
        // must not mark the in-flight job failed.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);

        var after = await store.GetAsync(job.Id);
        Assert.Equal("running", after!.Status);
        Assert.Null(after.Error);
    }

    private sealed class BlockingHandler : IExtractionJobHandler
    {
        public const string KindValue = "blocking-test";

        public string Kind => KindValue;

        public Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
