using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class StaleJobRecoveryTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public StaleJobRecoveryTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Running_worker_job_is_failed_on_startup_recovery()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var job = new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "plain_text",
            Status = "running",
            Phase = "dispatching",
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
        };
        db.ExtractionJobs.Add(job);
        await db.SaveChangesAsync();

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var recovery = new StaleJobRecoveryService(
            db,
            loggerFactory.CreateLogger<StaleJobRecoveryService>(),
            TimeProvider.System);

        await recovery.RunAsync(CancellationToken.None);

        var persisted = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal("failed", persisted.Status);
        Assert.Equal("Interrupted by a server restart", persisted.Error);
        Assert.NotNull(persisted.FinishedAt);
    }
}
