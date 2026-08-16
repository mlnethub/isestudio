using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Tests.Authentication;

/// <summary>
/// Web host used by the auth/access contract tests. Flips the persistence
/// provider to SQLite via configuration and gives each factory instance its
/// own unique on-disk SQLite database (deleted on dispose) so EF Core opens
/// a fresh connection without leaking providers across tests.
/// </summary>
public sealed class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "admin12345strong";
    public const string AdminDisplayName = "Test Admin";
    public const string OtherUsername = "alice";
    public const string OtherPassword = "alice12345strong";

    private readonly string _sqlitePath;
    private readonly IPasswordService? _passwordOverride;

    public AuthTestWebApplicationFactory() : this(passwordOverride: null)
    {
    }

    /// <summary>
    /// Construct with a custom <see cref="IPasswordService"/>. Used by the
    /// timing-safe login regression test to assert the controller calls
    /// <c>Verify</c> on every login attempt (so missing vs wrong password
    /// take the same time).
    /// </summary>
    public AuthTestWebApplicationFactory(IPasswordService? passwordOverride)
    {
        var rawPath = Path.Combine(
            Path.GetTempPath(),
            $"ontopilot-auth-tests-{Guid.NewGuid():N}.db");
        // SQLite connection-string parser is sensitive to backslashes.
        _sqlitePath = rawPath.Replace('\\', '/');
        _passwordOverride = passwordOverride;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OnToPilot:CookieSecure"] = "false",
                ["OnToPilot:SessionTtlHours"] = "1",
                ["OnToPilot:Persistence:Provider"] = "sqlite",
                // Use the explicit "Data Source=" form so the SQLite
                // connection-string parser doesn't choke on the raw path.
                ["OnToPilot:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
            });
        });

        builder.ConfigureServices(services =>
        {
            if (_passwordOverride is not null)
            {
                // Replace the production IPasswordService registration with
                // the spy/fake the test injected.
                var existing = services.Where(d => d.ServiceType == typeof(IPasswordService)).ToList();
                foreach (var desc in existing) services.Remove(desc);
                services.AddSingleton(_passwordOverride);
            }
        });
    }

    /// <summary>
    /// Builds the host (if needed) and returns a fresh
    /// <see cref="OnToPilotDbContext"/> against this factory's SQLite
    /// database. Ensures the schema exists.
    /// </summary>
    public OnToPilotDbContext CreateDbContext()
    {
        _ = CreateClient();
        var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        db.Database.EnsureCreated();
        return db;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
            catch { /* ignore — best effort */ }
        }
        base.Dispose(disposing);
    }
}