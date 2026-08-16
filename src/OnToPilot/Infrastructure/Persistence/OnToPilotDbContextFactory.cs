using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OnToPilot.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> when scaffolding migrations.
/// Builds a <see cref="OnToPilotDbContext"/> against the PostgreSQL provider
/// so the generated SQL uses Postgres-native column types
/// (<c>jsonb</c>, <c>bytea</c>, <c>timestamp with time zone</c>). The
/// connection string is read from the <c>OnToPilot__MigrationsConnection</c>
/// environment variable or falls back to a localhost default.
/// </summary>
public sealed class OnToPilotDbContextFactory : IDesignTimeDbContextFactory<OnToPilotDbContext>
{
    /// <inheritdoc />
    public OnToPilotDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("OnToPilot__MigrationsConnection")
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(OnToPilotDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new OnToPilotDbContext(options);
    }
}