using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Infrastructure;

/// <summary>
/// End-to-end regression tests proving that the startup-recovery services
/// (<see cref="ISEStudio.Infrastructure.Startup.StaleJobRecoveryService"/> and
/// <see cref="ISEStudio.Infrastructure.Startup.LegacyBackfillService"/>) are
/// actually invoked from <c>Program.cs</c> during host startup — not just
/// that the services work in isolation.
///
/// <para>The other seven tests in <see cref="StartupRecoveryTests"/> construct
/// the services by hand with a manual <see cref="ISEStudioDbContext"/>; they
/// would still pass if a future refactor accidentally deleted the two
/// <c>await ... .RunAsync(...)</c> calls in <c>Program.cs</c>. These tests
/// close that gap by booting the real ASP.NET Core host under a non-Testing
/// environment, seeding unclean state before host build, and asserting the
/// state is healed after the host finishes starting.</para>
/// </summary>
public sealed class StartupRecoveryHostTests
{
    [Fact]
    public async Task Startup_runs_stale_job_recovery_and_orphan_backfill_via_Program_cs()
    {
        // Arrange: spin up a real WebApplicationFactory<Program> under a
        // non-Testing environment so the if (!IsEnvironment("Testing"))
        // gate in Program.cs actually fires. The factory pre-seeds the
        // SQLite database (user, interrupted extraction job, orphan
        // document) BEFORE the host builds, so the bootstrap gate passes
        // and the recovery services see the seeded data.
        using var factory = new StartupRecoveryHostFactory();
        var preSeed = factory.PreSeed();

        var ksId = preSeed.KnowledgeSystemId;
        var interruptedJobId = preSeed.InterruptedJobId;
        var orphanDocumentId = preSeed.OrphanDocumentId;

        // Act: building the host triggers the recovery pipeline.
        // CreateClient() forces the host to start, which runs BootstrapAdminService,
        // StaleJobRecoveryService, and LegacyBackfillService in sequence.
        using var client = factory.CreateClient();

        // Assert: recover the post-startup state and verify the recovery
        // services healed what they were supposed to heal.
        var probe = factory.CreateDbContext();
        try
        {
            var interruptedJob = await probe.ExtractionJobs
                .AsNoTracking()
                .SingleAsync(j => j.Id == interruptedJobId);
            Assert.Equal("failed", interruptedJob.Status);
            Assert.Equal("Interrupted by a server restart", interruptedJob.Error);
            Assert.NotNull(interruptedJob.FinishedAt);

            var orphanDocument = await probe.Documents
                .AsNoTracking()
                .SingleAsync(d => d.Id == orphanDocumentId);
            Assert.NotNull(orphanDocument.KnowledgeSystemId);
            Assert.Equal(ksId, orphanDocument.KnowledgeSystemId);
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    /// <summary>
    /// Handle returned by <see cref="StartupRecoveryHostFactory.PreSeed"/>.
    /// Holds the IDs of the rows that were seeded so the test can assert
    /// post-startup state without ambiguity.
    /// </summary>
    public sealed record PreSeedHandle(
        Guid KnowledgeSystemId,
        Guid InterruptedJobId,
        Guid OrphanDocumentId);

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> that boots
    /// <c>Program.cs</c> under a non-Testing environment on a private SQLite
    /// file.
    /// </summary>
    /// <remarks>
    /// <para>The factory pre-seeds the database in <see cref="PreSeed"/> so
    /// the bootstrap gate (which refuses to start against an empty users
    /// table) sees a user and the two recovery services have observable
    /// state to act on. The SQLite file is created on disk so the same
    /// physical file is visible to both the pre-seed DbContext and the
    /// host's request-scope DbContext.</para>
    /// </remarks>
    private sealed class StartupRecoveryHostFactory : WebApplicationFactory<Program>
    {
        private readonly string _sqlitePath;

        public StartupRecoveryHostFactory()
        {
            var rawPath = Path.Combine(
                Path.GetTempPath(),
                $"isestudio-startup-recovery-{Guid.NewGuid():N}.db");
            _sqlitePath = rawPath.Replace('\\', '/');
        }

        /// <summary>
        /// Open the SQLite database, ensure the schema exists, and seed
        /// a user, an interrupted extraction job, and an orphan document.
        /// </summary>
        public PreSeedHandle PreSeed()
        {
            // Use the same physical SQLite file the host will use. Open
            // via DbContextOptions so the EF model is the source of truth
            // for the schema (no risk of drift between pre-seed and host).
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseSqlite($"Data Source={_sqlitePath}")
                .Options;

            using var db = new ISEStudioDbContext(options);
            db.Database.EnsureCreated();

            var ksId = Guid.NewGuid();
            db.KnowledgeSystems.Add(new KnowledgeSystemEntity
            {
                Id = ksId,
                LegacyId = TestLegacyIds.Next("knowledgesystem"),
                PublicId = "kstest" + Guid.NewGuid().ToString("N")[..10],
                Name = "ks-startup-recovery",
                Description = "",
                OwnerId = null,
                GraphIri = "http://isestudio.test/ks/" + Guid.NewGuid().ToString("N"),
                BaseIri = "http://isestudio.test/ks/" + Guid.NewGuid().ToString("N") + "/onto#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            // Seed a user so the bootstrap gate does NOT refuse to start.
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = "bootstrap-seed",
                DisplayName = "Bootstrap Seed",
                PasswordHash = "$2a$04$" + new string('0', 53),
                IsAdmin = true,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            // Seed an interrupted extraction job — StaleJobRecoveryService
            // must transition this to "failed" on startup.
            var interruptedJobId = Guid.NewGuid();
            db.ExtractionJobs.Add(new ExtractionJobEntity
            {
                Id = interruptedJobId,
                LegacyId = TestLegacyIds.Next("extractionjob"),
                KnowledgeSystemId = ksId,
                Kind = "tbox",
                Status = "running",
                Model = "test-model",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });

            // Seed an orphan document — LegacyBackfillService must bind
            // this to a knowledge system on startup.
            var orphanDocumentId = Guid.NewGuid();
            db.Documents.Add(new DocumentEntity
            {
                Id = orphanDocumentId,
                LegacyId = TestLegacyIds.Next("document"),
                KnowledgeSystemId = null,
                Sha256 = new string('c', 64),
                OriginalFilename = "orphan-at-startup.pdf",
                Folder = "/",
                Ext = "pdf",
                StoragePath = "cc/dd/" + new string('c', 64),
                UploadedAt = DateTimeOffset.UtcNow,
                ParseStatus = "pending",
            });

            db.SaveChanges();

            return new PreSeedHandle(ksId, interruptedJobId, orphanDocumentId);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Use "Staging" so the non-Testing recovery pipeline runs. The
            // bootstrap gate will pass because PreSeed created a user.
            builder.UseEnvironment("Staging");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ISEStudio:Persistence:Provider"] = "sqlite",
                    ["ISEStudio:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
                });
            });
        }

        /// <summary>
        /// Build the host and return a fresh DbContext against the same
        /// SQLite file. Used by the test to probe post-startup state.
        /// </summary>
        public ISEStudioDbContext CreateDbContext()
        {
            _ = CreateClient();
            var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            return db;
        }

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
}
