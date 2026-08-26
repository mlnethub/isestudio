using System.Text.Json;
using ISEStudio.Exports;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Exports;

/// <summary>
/// Fixture: shared SQLite <see cref="ISEStudioDbContext"/> factory +
/// per-test <see cref="ExportJobStore"/>. Tests mint their own
/// <see cref="KnowledgeSystemEntity"/> row via
/// <see cref="ExportJobStoreFixture.SeedKnowledgeSystemAsync"/> so the
/// per-test rows stay isolated (every test method runs against a
/// brand-new KS row, so ListAsync order assertions are deterministic).
/// </summary>
public sealed class ExportJobStoreFixture : IDisposable
{
    public SqliteContextFactory Contexts { get; }
    public ExportJobStore Jobs { get; }

    public ExportJobStoreFixture()
    {
        Contexts = new SqliteContextFactory();
        Jobs = new ExportJobStore(Contexts, TimeProvider.System);
    }

    /// <summary>
    /// Seed a fresh <see cref="KnowledgeSystemEntity"/> and return its
    /// PK. Explicit <c>LegacyId</c> values are still honored by the DB
    /// (D1(c): only unset values default to 0).
    /// </summary>
    public async Task<Guid> SeedKnowledgeSystemAsync()
    {
        var ksId = Guid.NewGuid();
        await using var db = Contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = ksId,
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Export fixture",
            GraphIri = "http://goodcrew.local/ks/export-fixture",
            BaseIri = "http://goodcrew.local/ks/export-fixture/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return ksId;
    }

    public void Dispose() => Contexts.Dispose();
}

public class ExportJobStoreTests : IClassFixture<ExportJobStoreFixture>
{
    private readonly ExportJobStoreFixture _fx;

    public ExportJobStoreTests(ExportJobStoreFixture fx) { _fx = fx; }

    [Fact]
    [Trait("Category", "Export")]
    public async Task CreateAsync_inserts_pending_row_with_default_legacy_id_zero()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var first = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        Assert.Equal("pending", first.Status);
        // D1(c): LegacyIdAllocator retired — the DB DEFAULT 0 fills the
        // column; uniqueness now comes from the Guid PK.
        Assert.Equal(0L, first.LegacyId);
        Assert.Equal(ExportLayer.TBox, first.Layer);
        Assert.Equal("nquads", first.Format);

        var second = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.ABox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        Assert.Equal(0L, second.LegacyId);

        // Prove the DB stored 0, not just the CLR default.
        await using (var db = _fx.Contexts.CreateDbContext())
        {
            Assert.Equal(0L, db.ExportJobs.Single(j => j.Id == first.Id).LegacyId);
            Assert.Equal(0L, db.ExportJobs.Single(j => j.Id == second.Id).LegacyId);
        }
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task GetAsync_returns_null_for_unknown_id()
    {
        var fetched = await _fx.Jobs.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(fetched);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task ResolveAsync_matches_by_guid_first_then_legacy_id()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.Bundle, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        await using (var db = _fx.Contexts.CreateDbContext())
        {
            var byGuid = await _fx.Jobs.ResolveAsync(
                db, ksId, job.Id.ToString(), CancellationToken.None);
            Assert.NotNull(byGuid);
            Assert.Equal(job.Id, byGuid!.Id);

            var byLegacy = await _fx.Jobs.ResolveAsync(
                db, ksId, job.LegacyId.ToString(), CancellationToken.None);
            Assert.NotNull(byLegacy);
            Assert.Equal(job.Id, byLegacy!.Id);

            var junk = await _fx.Jobs.ResolveAsync(
                db, ksId, "not-a-guid", CancellationToken.None);
            Assert.Null(junk);

            var empty = await _fx.Jobs.ResolveAsync(
                db, ksId, "", CancellationToken.None);
            Assert.Null(empty);
        }
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task ListAsync_returns_newest_first_by_legacy_id_desc()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var a = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        var b = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.ABox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        var c = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.Vocabulary, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        // D1(c): new rows all carry legacy_id 0, so give the ordering
        // coverage explicit distinct legacy ids (historical-data style).
        await using (var db = _fx.Contexts.CreateDbContext())
        {
            db.ExportJobs.Single(j => j.Id == a.Id).LegacyId = 1;
            db.ExportJobs.Single(j => j.Id == b.Id).LegacyId = 2;
            db.ExportJobs.Single(j => j.Id == c.Id).LegacyId = 3;
            await db.SaveChangesAsync();
        }

        var rows = await _fx.Jobs.ListAsync(ksId, CancellationToken.None);
        Assert.Equal(new[] { c.Id, b.Id, a.Id },
            rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task MarkRunningAsync_sets_status_and_started_at()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        await _fx.Jobs.MarkRunningAsync(job.Id, CancellationToken.None);
        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("running", fetched!.Status);
        Assert.NotNull(fetched.StartedAt);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task UpdateProgressAsync_writes_counter()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        await _fx.Jobs.UpdateProgressAsync(job.Id, 42, CancellationToken.None);
        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.Equal(42, fetched!.ProcessedStatements);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task RecordFilesAsync_persists_files_json()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        var files = new List<ExportFileEntry>
        {
            new("tbox-0000.nq", ExportLayer.TBox, 10L, 256L, "deadbeef"),
            new("manifest.json", "manifest", 0L, 128L, "feedface"),
        };
        await _fx.Jobs.RecordFilesAsync(job.Id, files, CancellationToken.None);

        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.NotNull(fetched!.Files);
        // ExportJobStore serialises with the SnakeCaseLower policy so the
        // persisted JSON matches the wire shape ExportOut hands the
        // frontend.
        var names = fetched.Files!.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "tbox-0000.nq", "manifest.json" }, names);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task MarkCompletedAsync_sets_terminal_state()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        await _fx.Jobs.MarkCompletedAsync(job.Id, totalStatements: 123,
            CancellationToken.None);
        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", fetched!.Status);
        Assert.Equal(123, fetched.TotalStatements);
        Assert.Equal(123, fetched.ProcessedStatements);
        Assert.NotNull(fetched.FinishedAt);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task MarkFailedAsync_captures_error()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        await _fx.Jobs.MarkFailedAsync(job.Id, "boom", CancellationToken.None);
        var fetched = await _fx.Jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.Equal("failed", fetched!.Status);
        Assert.Equal("boom", fetched.Error);
        Assert.NotNull(fetched.FinishedAt);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task WaitAsync_returns_when_status_is_completed()
    {
        var ksId = await _fx.SeedKnowledgeSystemAsync();
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);

        // Kick off a fast mark-completed on the thread pool so the
        // background waiter has something to race.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await _fx.Jobs.MarkCompletedAsync(job.Id, 1, CancellationToken.None);
        });

        var finished = await _fx.Jobs.WaitAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", finished.Status);
    }
}