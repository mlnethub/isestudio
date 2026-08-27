using System.Text;
using ISEStudio.Exports;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Extraction;

namespace ISEStudio.Tests.Exports;

/// <summary>
/// Minimal fixture for the read-path fallback tests: a SQLite context
/// factory, a temp export root, and an <see cref="ExportJobStore"/>.
///
/// <para>Deliberately does NOT open an Oxigraph <c>StoreWrapper</c>
/// the way <see cref="ExportServiceFixture"/> does — these tests never
/// invoke <see cref="ExportRunner"/> (they hand-build completed job rows
/// and pre-place bytes on disk), and holding a second live RocksDB handle
/// concurrently with the neighbouring export fixture destabilised the
/// test host. <see cref="ExportRunner"/> takes a nullable store, so the
/// runner is constructed store-less purely to satisfy the
/// <see cref="ExportService"/> constructor.</para>
/// </summary>
public sealed class ExportLegacyLayoutFixture : IDisposable
{
    public string Root { get; }
    public SqliteContextFactory Contexts { get; }
    public ExportArtifactStore Artifacts { get; }
    public ExportJobStore Jobs { get; }

    private readonly ExportRunner _runner;

    public ExportLegacyLayoutFixture()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "isestudio-export-legacy-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        Contexts = new SqliteContextFactory();
        Artifacts = new ExportArtifactStore(Path.Combine(Root, "exports"));
        Jobs = new ExportJobStore(Contexts, TimeProvider.System);
        _runner = new ExportRunner(
            Jobs, Artifacts, store: null, releaseArtifacts: null, TimeProvider.System);
    }

    public void Dispose()
    {
        Contexts.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    /// <summary>Seed an isolated knowledge system (fresh PublicId per call).</summary>
    public KnowledgeSystemEntity SeedKnowledgeSystem()
    {
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Export legacy-layout fixture",
            GraphIri = "http://goodcrew.local/ks/export-legacy",
            BaseIri = "http://goodcrew.local/ks/export-legacy/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        using var db = Contexts.CreateDbContext();
        db.KnowledgeSystems.Add(ks);
        db.SaveChanges();
        return ks;
    }

    /// <summary>Scoped <see cref="ExportService"/> over this fixture.</summary>
    public ExportService CreateService() =>
        new(Contexts.CreateDbContext(), Jobs, _runner, Artifacts);
}

/// <summary>
/// Regression cover for the Phase 3 read-time layout fallback. Phase 3
/// dropped the per-job <c>legacy_id</c> subdirectory from the export path,
/// so shards written before the cutover live at
/// <c>{root}/{publicId}/0/{layer}-NNNN.nq</c> while
/// <see cref="ExportArtifactStore"/> now reads
/// <c>{root}/{publicId}/{layer}-NNNN.nq</c>. Without the fallback those
/// completed jobs would silently 404.
/// </summary>
public class ExportServiceLegacyLayoutTests : IClassFixture<ExportLegacyLayoutFixture>
{
    private static readonly byte[] Sentinel = Encoding.UTF8.GetBytes(
        "<http://ex/legacy> <http://ex/p> <http://ex/o> <http://ex/g> .\n");

    private readonly ExportLegacyLayoutFixture _fx;

    public ExportServiceLegacyLayoutTests(ExportLegacyLayoutFixture fx) { _fx = fx; }

    /// <summary>
    /// Mint a completed job whose <c>Files</c> list advertises
    /// <c>tbox-0000.nq</c> (so the download path's file-list guard passes)
    /// without writing anything at the flat path.
    /// </summary>
    private async Task<Guid> SeedCompletedJobAsync(Guid ksId)
    {
        var job = await _fx.Jobs.CreateAsync(
            ksId, releaseId: null, layer: ExportLayer.TBox, shardSize: 100_000,
            format: "nquads", createdById: null, createdByName: "tester",
            CancellationToken.None);
        await _fx.Jobs.RecordFilesAsync(job.Id, new[]
        {
            new ExportFileEntry(
                Name: "tbox-0000.nq",
                Layer: ExportLayer.TBox,
                Statements: 1,
                Bytes: Sentinel.Length,
                Sha256: "deadbeef"),
        }, CancellationToken.None);
        await _fx.Jobs.MarkCompletedAsync(job.Id, 1, CancellationToken.None);
        return job.Id;
    }

    /// <summary>
    /// Write <paramref name="fileName"/> under the pre-Phase-3 numeric
    /// subdirectory <paramref name="subdir"/> for this KS.
    /// </summary>
    private void WriteLegacyShard(
        string publicId, string subdir, string fileName, byte[] payload)
    {
        var dir = Path.Combine(_fx.Artifacts.JobPath(publicId), subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), payload);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_falls_back_to_single_numeric_subdir()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var jobId = await SeedCompletedJobAsync(ks.Id);

        // Pre-cutover on-disk shape: exactly one numeric subdir ("0",
        // because post-Phase-2 every job row carried legacy_id = 0).
        WriteLegacyShard(ks.PublicId, "0", "tbox-0000.nq", Sentinel);

        var ex = await Assert.ThrowsAsync<ISEStudio.Api.ExportFilePayloadException>(() =>
            svc.DownloadFileAsync(ks.Id, jobId.ToString(), "tbox-0000.nq",
                CancellationToken.None));
        Assert.Equal("tbox-0000.nq", ex.FileName);
        Assert.Equal("application/n-quads", ex.MediaType);
        // Sentinel bytes prove the read came from the legacy subdir (no
        // file was ever written at the flat path).
        Assert.Equal(Sentinel, ex.Bytes);
    }

    [Fact]
    [Trait("Category", "Export")]
    public async Task DownloadFileAsync_404s_when_multiple_numeric_subdirs()
    {
        using var svc = _fx.CreateService();
        var ks = _fx.SeedKnowledgeSystem();
        var jobId = await SeedCompletedJobAsync(ks.Id);

        // Two candidate job dirs → ambiguous. Serving either would risk
        // handing one job's artefact out under another job's id, so the
        // contract stays 404.
        WriteLegacyShard(ks.PublicId, "0", "tbox-0000.nq", Sentinel);
        WriteLegacyShard(ks.PublicId, "1", "tbox-0000.nq", Sentinel);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.DownloadFileAsync(ks.Id, jobId.ToString(), "tbox-0000.nq",
                CancellationToken.None));
    }
}
