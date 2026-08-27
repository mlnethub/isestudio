using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Providers;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Providers;

/// <summary>
/// Real-PostgreSQL guard for concurrent <see cref="ProviderService.CreateAsync"/>
/// calls. Historically this exercised the <c>ux_provider_legacy_id</c>
/// unique-constraint race; Guid PK Phase 2 (D1(c)) dropped the
/// <c>ux_*_legacy_id</c> indexes and retired the allocator, so every new
/// row now lands on <c>legacy_id = 0</c> by DB DEFAULT. The concurrent
/// inserts must still all succeed and persist durably.
/// </summary>
/// <remarks>
/// <para>
/// Two concurrent calls used to both try to write <c>legacy_id = 0</c>
/// through the legacy path and the second INSERT was rejected by the
/// <c>ux_provider_legacy_id</c> UNIQUE index with <c>SqlState = 23505</c>.
/// SQLite's single-writer model hides the race, so this test needs a real
/// PostgreSQL backend to expose it.
/// </para>
/// <para>
/// Phase 2 removed the constraint itself: concurrent inserts of the same
/// <c>legacy_id = 0</c> are now legal and must each land on a durable,
/// individually-identifiable row (distinct Guid PKs).
/// </para>
/// </remarks>
[Trait("Category", "Persistence")]
public sealed class ProviderServicePgIntegrationTests : IAsyncLifetime
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

    private ISEStudioDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new ISEStudioDbContext(options);
    }

    /// <summary>
    /// Eight concurrent <see cref="ProviderService.CreateAsync"/> calls
    /// must all land on a durably-persisted row even though every row now
    /// shares <c>legacy_id = 0</c> (the <c>ux_provider_legacy_id</c>
    /// UNIQUE index is gone — Phase 2 D1(c)).
    /// </summary>
    [Fact]
    public async Task Pg_concurrent_provider_create_allows_shared_zero_legacy_id()
    {
        // Bring the schema up once via a short-lived context.
        await using (var setup = OpenContext())
        {
            await setup.Database.MigrateAsync();
        }

        const int parallelism = 8;
        var tasks = new Task[parallelism];

        for (var i = 0; i < parallelism; i++)
        {
            var captured = i;
            tasks[i] = Task.Run(async () =>
            {
                await using var ctx = OpenContext();
                // ProviderService.CreateAsync does not touch IHttpClientFactory
                // (only TestAsync does), so a null factory is safe here.
                var svc = new ProviderService(ctx, TimeProvider.System, null!);
                await svc.CreateAsync(new ProviderCreateRequest(
                    Name: $"p-{captured}-{Guid.NewGuid():N}",
                    Kind: ProviderService.KindLlm,
                    BaseUrl: "https://example.invalid",
                    Model: "test-model",
                    ApiKey: "sk-test",
                    ConcurrencyLimit: 4), default);
            });
        }

        // Historical failure mode: DbUpdateException with
        // PostgreSqlException SqlState=23505, ConstraintName=ux_provider_legacy_id
        // on the second concurrent SaveChanges. Phase 2 removed the
        // constraint — the inserts must all complete cleanly.
        await Task.WhenAll(tasks);

        // All 8 rows persisted under distinct Guid PKs — proving the
        // Guid keying is safe under real Postgres concurrency.
        await using var verify = OpenContext();
        var ids = await verify.Providers
            .Select(p => p.Id)
            .ToListAsync();
        Assert.Equal(parallelism, ids.Count);
        Assert.Equal(parallelism, ids.Distinct().Count());
    }
}
