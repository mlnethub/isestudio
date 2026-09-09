using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Infrastructure;

/// <summary>
/// Smoke guard for the runtime RDF dependency boundary: booting the real
/// <c>Program.cs</c> host (under SQLite) must NOT create an on-disk RDF
/// store — neither the configured <c>ISEStudio:Storage:RdfRoot</c> nor the
/// legacy <c>data/rdf</c> fallback. PostgreSQL is the runtime-authoritative
/// store; the only projects that may open an embedded graph store are
/// <c>ISEStudio.Migration</c> and <c>ISEStudio.OxigraphProbe</c>, neither of
/// which this host references. A regression that re-introduces an eager
/// RocksDB-style store would fail this test on the first <c>CreateClient()</c>.
/// </summary>
public sealed class NoDiskRdfStartupSmokeTests
{
    [Fact]
    public async Task Startup_under_sqlite_does_not_create_data_rdf()
    {
        using var factory = new NoDiskRdfFactory();
        factory.PreSeed();

        // CreateClient forces the full host startup pipeline (bootstrap
        // gate, hosted services, recovery services) to run.
        using var client = factory.CreateClient();
        _ = client; // the act is host startup itself

        var environment = factory.Services.GetRequiredService<IHostEnvironment>();
        var contentRootRdf = Path.Combine(environment.ContentRootPath, "data", "rdf");

        Assert.False(
            Directory.Exists(factory.RdfRoot),
            $"Startup must not create the configured RDF root '{factory.RdfRoot}'.");
        Assert.False(
            Directory.Exists(contentRootRdf),
            $"Startup must not create the legacy '{contentRootRdf}' directory.");
    }

    /// <summary>
    /// Boots <c>Program.cs</c> under a non-Testing environment on a private
    /// SQLite file, with <c>ISEStudio:Storage:RdfRoot</c> redirected to a
    /// temp path so a failing assertion never touches the repo tree.
    /// </summary>
    private sealed class NoDiskRdfFactory : WebApplicationFactory<Program>
    {
        private readonly string _sqlitePath;

        public NoDiskRdfFactory()
        {
            var rawPath = Path.Combine(
                Path.GetTempPath(),
                $"isestudio-no-disk-rdf-{Guid.NewGuid():N}.db");
            _sqlitePath = rawPath.Replace('\\', '/');
            RdfRoot = Path.Combine(
                Path.GetTempPath(),
                $"isestudio-no-disk-rdf-{Guid.NewGuid():N}",
                "data", "rdf");
        }

        /// <summary>Configured RDF root — asserted not to exist after startup.</summary>
        public string RdfRoot { get; }

        /// <summary>
        /// Seed a user so the bootstrap gate (which refuses to start
        /// against an empty users table) passes. Mirrors
        /// <c>StartupRecoveryHostFactory.PreSeed</c>.
        /// </summary>
        public void PreSeed()
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseSqlite($"Data Source={_sqlitePath}")
                .Options;

            using var db = new ISEStudioDbContext(options);
            db.Database.EnsureCreated();
            db.Users.Add(new UserEntity
            {
                Username = "bootstrap-seed",
                DisplayName = "Bootstrap Seed",
                PasswordHash = "$2a$04$" + new string('0', 53),
                IsAdmin = true,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "Staging" so the non-Testing startup pipeline runs exactly
            // as it does in production.
            builder.UseEnvironment("Staging");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ISEStudio:Persistence:Provider"] = "sqlite",
                    ["ISEStudio:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
                    ["ISEStudio:Storage:RdfRoot"] = RdfRoot,
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
                catch { /* ignore — best effort */ }
                try { if (Directory.Exists(RdfRoot)) Directory.Delete(RdfRoot, recursive: true); }
                catch { /* ignore — best effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
