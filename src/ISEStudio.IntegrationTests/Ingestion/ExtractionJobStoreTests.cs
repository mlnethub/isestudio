using ISEStudio.Extraction;
using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class ExtractionJobStoreTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public ExtractionJobStoreTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextAsync_claims_pending_and_sets_running_and_dispatching()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();

        var job = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "tbox",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ExtractionJobs.Add(job);
        await db.SaveChangesAsync();

        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var claimed = await store.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed!.Id);
        Assert.Equal("running", claimed.Status);
        Assert.Equal("dispatching", claimed.Phase);

        var persisted = await db.ExtractionJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal("running", persisted.Status);
        Assert.Equal("dispatching", persisted.Phase);
    }

    [Fact]
    public async Task ClaimNextAsync_only_one_store_claims_the_same_job_concurrently()
    {
        // Seed one pending job
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var pendingJob = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "tbox",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.ExtractionJobs.Add(pendingJob);
        await seedDb.SaveChangesAsync();

        // Build two separate service providers to simulate concurrent stores
        await using var services1 = _fixture.BuildServices();
        await using var services2 = _fixture.BuildServices();

        await using var scope1 = services1.CreateAsyncScope();
        await using var scope2 = services2.CreateAsyncScope();

        var factory1 = scope1.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var factory2 = scope2.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();

        var store1 = new ExtractionJobStore(factory1, TimeProvider.System);
        var store2 = new ExtractionJobStore(factory2, TimeProvider.System);

        var t1 = Task.Run(() => store1.ClaimNextAsync(CancellationToken.None));
        var t2 = Task.Run(() => store2.ClaimNextAsync(CancellationToken.None));

        await Task.WhenAll(t1, t2);

        var results = new[] { await t1, await t2 };
        var nonNull = results.Count(r => r is not null);
        Assert.Equal(1, nonNull);

        // Verify persisted state: exactly one running row
        await using var verifyServices = _fixture.BuildServices();
        await using var verifyScope = verifyServices.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var runningCount = await verifyDb.ExtractionJobs.CountAsync(j => j.Status == "running");
        Assert.Equal(1, runningCount);
    }

    [Fact]
    public async Task ClaimNextAsync_returns_null_when_queue_empty()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var store = new ExtractionJobStore(factory, TimeProvider.System);

        var claimed = await store.ClaimNextAsync(CancellationToken.None);
        Assert.Null(claimed);
    }

    [Fact]
    public async Task ClaimNextAsync_can_scope_claims_to_a_worker_kind()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var plainTextJob = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "plain_text",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var tboxJob = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "tbox",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(1),
        };
        db.ExtractionJobs.AddRange(plainTextJob, tboxJob);
        await db.SaveChangesAsync();

        var store = new ExtractionJobStore(factory, TimeProvider.System);
        var claimed = await store.ClaimNextAsync(
            CancellationToken.None,
            PlainTextIngestionJobProcessor.Kind);

        Assert.NotNull(claimed);
        Assert.Equal(plainTextJob.Id, claimed!.Id);
        var tboxStatus = await db.ExtractionJobs
            .Where(job => job.Id == tboxJob.Id)
            .Select(job => job.Status)
            .SingleAsync();
        Assert.Equal("pending", tboxStatus);

        db.ExtractionJobs.RemoveRange(plainTextJob, tboxJob);
        await db.SaveChangesAsync();
    }
}
