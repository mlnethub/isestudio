using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Helpers for building isolated <see cref="ISEStudioDbContext"/> instances backed by
/// an in-memory SQLite database. Each call yields a fresh context backed by a fresh
/// database connection, so tests never share schema state.
/// </summary>
public static class DbContextFactory
{
    /// <summary>
    /// Build a fresh <see cref="ISEStudioDbContext"/> against an in-memory SQLite database.
    /// The connection stays alive for the lifetime of the context, so the in-memory
    /// database persists while the context is in use.
    /// </summary>
    public static ISEStudioDbContext CreateSqlite()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new ISEStudioDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}