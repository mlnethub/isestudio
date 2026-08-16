using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Infrastructure.Startup;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Infrastructure;

/// <summary>
/// Tests for the startup-recovery hosted services: bootstrap admin
/// recovery, stale job recovery, and legacy backfill of orphan documents.
/// </summary>
public sealed class StartupRecoveryTests
{
    // -------------------------------------------------------------------------
    // BootstrapAdminService
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_returns_AlreadyBootstrapped_when_users_table_has_rows()
    {
        using var db = DbContextFactory.CreateSqlite();
        db.Users.Add(NewUser());
        await db.SaveChangesAsync();

        var service = new BootstrapAdminService(db, NullLogger<BootstrapAdminService>.Instance);
        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, outcome);
        Assert.False(service.RequiresExit);
        Assert.Equal(0, service.ExitCode);
    }

    [Fact]
    public async Task Bootstrap_returns_BootstrapRequired_when_users_table_is_empty()
    {
        using var db = DbContextFactory.CreateSqlite();
        var service = new BootstrapAdminService(db, NullLogger<BootstrapAdminService>.Instance);

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BootstrapOutcome.BootstrapRequired, outcome);
        Assert.True(service.RequiresExit);
    }

    [Fact]
    public async Task Bootstrap_does_not_create_a_default_admin_when_users_table_is_empty()
    {
        using var db = DbContextFactory.CreateSqlite();
        var service = new BootstrapAdminService(db, NullLogger<BootstrapAdminService>.Instance);

        await service.RunAsync(CancellationToken.None);

        // No user rows — empty installs MUST NOT land on a default password.
        Assert.Empty(db.Users);
    }

    [Fact]
    public void Bootstrap_required_exit_code_is_documented_and_nonzero()
    {
        // Operators and orchestrators read this constant to translate the
        // process exit into a remediation action ("seed the first admin
        // manually"). It must be a positive, non-zero value so shells,
        // Kubernetes, and systemd all distinguish it from success.
        Assert.True(BootstrapAdminService.BootstrapRequiredExitCode > 0,
            $"Bootstrap exit code must be positive; got {BootstrapAdminService.BootstrapRequiredExitCode}");
        Assert.NotEqual(0, BootstrapAdminService.BootstrapRequiredExitCode);
        // Documented: see the XML doc on the constant.
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_when_users_table_is_empty()
    {
        using var db = DbContextFactory.CreateSqlite();
        var service = new BootstrapAdminService(db, NullLogger<BootstrapAdminService>.Instance);

        var first = await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BootstrapOutcome.BootstrapRequired, first);
        Assert.Equal(BootstrapOutcome.BootstrapRequired, second);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_when_users_already_seeded()
    {
        using var db = DbContextFactory.CreateSqlite();
        db.Users.Add(NewUser());
        await db.SaveChangesAsync();

        var service = new BootstrapAdminService(db, NullLogger<BootstrapAdminService>.Instance);

        var first = await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, first);
        Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, second);
        // The seed step MUST NOT duplicate the existing user row.
        Assert.Single(db.Users);
    }

    // -------------------------------------------------------------------------
    // StaleJobRecoveryService
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Startup_marks_interrupted_work_failed_without_touching_completed_rows()
    {
        using var db = DbContextFactory.CreateSqlite();
        await SeedJobsAsync(db, ("running", "extract"), ("completed", "extract"), ("pending", "export"));

        var service = new StaleJobRecoveryService(db, NullLogger<StaleJobRecoveryService>.Instance, TimeProvider.System);
        await service.RunAsync(CancellationToken.None);

        // Recovery must mark every interrupted row "failed" and leave the
        // completed row alone. We verify directly against the table rather
        // than via a "find by original status" lookup because every
        // interrupted row gets the same error message.
        var extractionJobs = await db.ExtractionJobs.OrderBy(j => j.Status).ToListAsync();
        Assert.Equal(2, extractionJobs.Count);
        Assert.Equal("completed", extractionJobs[0].Status);
        Assert.Equal("failed", extractionJobs[1].Status);

        var exportJobs = await db.ExportJobs.ToListAsync();
        Assert.Single(exportJobs);
        Assert.Equal("failed", exportJobs[0].Status);
    }

    [Fact]
    public async Task Startup_marks_interrupted_release_deployments_failed()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ks = NewKnowledgeSystem(db);
        // Each release has at most one deployment (the SQLModel contract
        // declares a unique index on ReleaseDeployment.ReleaseId), so seed
        // a separate release per deployment.
        var provisioningRelease = new OntologyReleaseEntity
        {
            LegacyId = TestLegacyIds.Next("ontologyrelease"),
            KnowledgeSystemId = ks.Id,
            Version = "draft-prov",
            Status = "published",
            Title = "Provisioning release",
            Notes = "",
            SnapshotDir = "/tmp/snap-prov",
            CreatedByName = "system",
            ReviewedByName = "system",
            PublishedByName = "system",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        var activeRelease = new OntologyReleaseEntity
        {
            LegacyId = TestLegacyIds.Next("ontologyrelease"),
            KnowledgeSystemId = ks.Id,
            Version = "draft-active",
            Status = "published",
            Title = "Active release",
            Notes = "",
            SnapshotDir = "/tmp/snap-active",
            CreatedByName = "system",
            ReviewedByName = "system",
            PublishedByName = "system",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.OntologyReleases.Add(provisioningRelease);
        db.OntologyReleases.Add(activeRelease);
        await db.SaveChangesAsync();

        db.ReleaseDeployments.Add(new ReleaseDeploymentEntity
        {
            LegacyId = TestLegacyIds.Next("releasedeployment"),
            KnowledgeSystemId = ks.Id,
            ReleaseId = provisioningRelease.Id,
            Status = "provisioning",
            TboxGraphIri = "http://test/tbox",
            VocabularyGraphIri = "http://test/vocab",
            AboxGraphIri = "http://test/abox",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.ReleaseDeployments.Add(new ReleaseDeploymentEntity
        {
            LegacyId = TestLegacyIds.Next("releasedeployment"),
            KnowledgeSystemId = ks.Id,
            ReleaseId = activeRelease.Id,
            Status = "active",
            TboxGraphIri = "http://test/tbox2",
            VocabularyGraphIri = "http://test/vocab2",
            AboxGraphIri = "http://test/abox2",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new StaleJobRecoveryService(db, NullLogger<StaleJobRecoveryService>.Instance, TimeProvider.System);
        await service.RunAsync(CancellationToken.None);

        var rows = db.ReleaseDeployments.OrderBy(r => r.TboxGraphIri).ToList();
        Assert.Equal("failed", rows[0].Status);
        Assert.Equal("active", rows[1].Status);
    }

    [Fact]
    public async Task Startup_recovery_is_idempotent_for_already_failed_rows()
    {
        using var db = DbContextFactory.CreateSqlite();
        await SeedJobsAsync(db, ("running", "extract"));
        var service = new StaleJobRecoveryService(db, NullLogger<StaleJobRecoveryService>.Instance, TimeProvider.System);

        await service.RunAsync(CancellationToken.None);
        var firstFinishedAt = (await db.ExtractionJobs.SingleAsync()).FinishedAt;
        await service.RunAsync(CancellationToken.None);
        var secondFinishedAt = (await db.ExtractionJobs.SingleAsync()).FinishedAt;

        Assert.Equal(firstFinishedAt, secondFinishedAt);
    }

    // -------------------------------------------------------------------------
    // LegacyBackfillService
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Backfill_binds_orphan_document_to_a_knowledge_system()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ks = NewKnowledgeSystem(db);
        var doc = new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = null, // orphan
            Sha256 = new string('a', 64),
            OriginalFilename = "orphan.pdf",
            Folder = "/",
            Ext = "pdf",
            StoragePath = "aa/bb/" + new string('a', 64),
            UploadedAt = DateTimeOffset.UtcNow,
            ParseStatus = "pending",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var service = new LegacyBackfillService(db, NullLogger<LegacyBackfillService>.Instance);
        await service.RunAsync(CancellationToken.None);

        var reloaded = db.Documents.Single();
        Assert.NotNull(reloaded.KnowledgeSystemId);
        Assert.Equal(ks.Id, reloaded.KnowledgeSystemId);
    }

    [Fact]
    public async Task Backfill_is_a_noop_when_no_orphans_exist()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ks = NewKnowledgeSystem(db);
        db.Documents.Add(new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = ks.Id, // already bound
            Sha256 = new string('b', 64),
            OriginalFilename = "bound.pdf",
            Folder = "/",
            Ext = "pdf",
            StoragePath = "bb/cc/" + new string('b', 64),
            UploadedAt = DateTimeOffset.UtcNow,
            ParseStatus = "pending",
        });
        await db.SaveChangesAsync();

        var service = new LegacyBackfillService(db, NullLogger<LegacyBackfillService>.Instance);
        await service.RunAsync(CancellationToken.None);

        var reloaded = db.Documents.Single();
        Assert.Equal(ks.Id, reloaded.KnowledgeSystemId); // unchanged
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static UserEntity NewUser() => new()
    {
        LegacyId = TestLegacyIds.Next("users"),
        Username = "admin" + Guid.NewGuid().ToString("N")[..6],
        DisplayName = "Admin",
        PasswordHash = "$2a$04$" + new string('0', 53),
        IsAdmin = true,
        Active = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static KnowledgeSystemEntity NewKnowledgeSystem(OnToPilotDbContext db) =>
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = "kstest" + Guid.NewGuid().ToString("N")[..10],
            Name = "ks-test",
            Description = "",
            OwnerId = null,
            GraphIri = "http://ontopilot.test/ks/" + Guid.NewGuid().ToString("N"),
            BaseIri = "http://ontopilot.test/ks/" + Guid.NewGuid().ToString("N") + "/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).Entity;

    private static async Task SeedJobsAsync(
        OnToPilotDbContext db,
        params (string status, string kind)[] jobs)
    {
        var ks = NewKnowledgeSystem(db);
        foreach (var (status, kind) in jobs)
        {
            if (kind == "export")
            {
                db.ExportJobs.Add(new ExportJobEntity
                {
                    LegacyId = TestLegacyIds.Next("exportjob"),
                    KnowledgeSystemId = ks.Id,
                    Layer = "tbox",
                    Format = "nquads",
                    Status = status,
                    OutputDir = "/tmp",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                db.ExtractionJobs.Add(new ExtractionJobEntity
                {
                    LegacyId = TestLegacyIds.Next("extractionjob"),
                    KnowledgeSystemId = ks.Id,
                    Kind = kind,
                    Status = status,
                    Model = "test-model",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync();
    }
}