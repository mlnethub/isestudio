using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnToPilot.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> when scaffolding migrations.
/// Builds a <see cref="OnToPilotDbContext"/> against the PostgreSQL provider
/// so the generated SQL uses Postgres-native column types
/// (<c>jsonb</c>, <c>bytea</c>, <c>timestamp with time zone</c>).
/// </summary>
/// <remarks>
/// DESIGN-TIME ONLY. This factory is intentionally not wired into DI and is
/// never used by the running application. The hardcoded
/// <c>postgres/postgres</c> fallback exists only so a developer with no
/// environment configuration can still scaffold migrations against a local
/// Postgres. Production connection strings come from
/// <c>OnToPilot:Persistence:ConnectionString</c> via
/// <c>Program.cs</c>.
/// </remarks>
public sealed class OnToPilotDbContextFactory : IDesignTimeDbContextFactory<OnToPilotDbContext>
{
    /// <inheritdoc />
    public OnToPilotDbContext CreateDbContext(string[] args)
    {
        // DESIGN-TIME ONLY: a local default so `dotnet ef migrations add`
        // works out of the box on developer machines. Override via the
        // OnToPilot__MigrationsConnection env var to point at a real Postgres.
        var connection = Environment.GetEnvironmentVariable("OnToPilot__MigrationsConnection")
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(OnToPilotDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new OnToPilotDbContext(options);
    }
}