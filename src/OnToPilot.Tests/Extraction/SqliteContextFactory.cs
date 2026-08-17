using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> over an in-memory SQLite database
/// scoped to this factory instance via a unique shared-cache name. The
/// extraction orchestrator runs its work on a background task that outlives
/// the caller's scope, so it resolves a fresh
/// <see cref="OnToPilotDbContext"/> per operation rather than sharing the
/// caller's. Each context opens its own connection to the shared database
/// so SQLite's internal write lock is the only contention point — EF Core
/// no longer races against another context on the same connection.
/// </summary>
public sealed class SqliteContextFactory : IDbContextFactory<OnToPilotDbContext>, IDisposable
{
    private readonly string _connectionString;

    public SqliteContextFactory()
    {
        // Each factory gets its own shared-cache name so the in-memory
        // database is private to this test fixture. Without the unique
        // name every test in the process would share the same physical
        // store and primary-key auto-increment would collide between
        // unrelated fixtures.
        var cacheName = $"ontopilot-extraction-{Guid.NewGuid():N}";
        _connectionString = $"Data Source=file:memdb-{cacheName}?mode=memory&cache=shared";

        // Bootstrap the schema so the first real caller already sees the
        // tables; subsequent calls skip the EnsureCreated path entirely.
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var bootstrap = new OnToPilotDbContext(new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseSqlite(connection)
            .Options);
        bootstrap.Database.EnsureCreated();
    }

    /// <inheritdoc />
    public OnToPilotDbContext CreateDbContext()
    {
        // Each context opens its own connection to the shared in-memory
        // database so the EF Core provider initialises SQLite user
        // functions on its own thread — concurrent calls no longer race
        // inside Microsoft.Data.Sqlite's connection state. SQLite's own
        // write-lock serialises the queries; for the extraction test
        // volume (one row per call, no cross-call transactions) this is
        // fast enough that no further locking is required.
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return new OnToPilotDbContext(new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseSqlite(connection)
            .Options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
