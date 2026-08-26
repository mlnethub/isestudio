using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Providers;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Providers;

/// <summary>
/// Real-PostgreSQL guard against the
/// <c>ux_provider_legacy_id</c> unique-constraint race that surfaced when
/// two operators concurrently hit <see cref="ProviderService.CreateAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProviderService.CreateAsync"/> used to construct a new
/// <c>ProviderEntity</c> with <c>LegacyId</c> left at its default
/// <c>0</c>, call <c>_db.Providers.Add(entity)</c>, and let EF Core
/// <c>SaveChangesAsync</c> INSERT it. Two concurrent calls therefore both
/// tried to write <c>legacy_id = 0</c>; the second INSERT was rejected by
/// the <c>ux_provider_legacy_id</c> UNIQUE index with
/// <c>SqlState = 23505</c>. SQLite's single-writer model hides the race,
/// so this test needs a real PostgreSQL backend to expose it.
/// </para>
/// <para>
/// The fix routes provider creation through a single
/// <c>SaveChangesAsync</c> per call. After the fix, this test should
/// pass: every contended call lands on a distinct, durably persisted
/// row.
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
    /// Two or more concurrent <see cref="ProviderService.CreateAsync"/>
    /// calls must each land on a distinct, durably-persisted row. Before
    /// the fix this trips <c>ux_provider_legacy_id</c> with
    /// <c>SqlState 23505</c> on the second INSERT.
    /// </summary>
    [Fact]
    public async Task Pg_concurrent_provider_create_does_not_violate_legacy_id_unique()
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

        // Before the fix: throws DbUpdateException with PostgreSqlException
        // SqlState=23505, ConstraintName=ux_provider_legacy_id on the
        // second concurrent SaveChanges. After the fix: completes cleanly.
        await Task.WhenAll(tasks);

        // Every row has a distinct LegacyId — proves the allocator actually
        // serialised the 8 concurrent inserts rather than papering over the
        // race with a single sequential retry loop.
        await using var verify = OpenContext();
        var legacyIds = await verify.Providers
            .Select(p => p.LegacyId)
            .ToListAsync();
        Assert.Equal(parallelism, legacyIds.Distinct().Count());
    }
}
