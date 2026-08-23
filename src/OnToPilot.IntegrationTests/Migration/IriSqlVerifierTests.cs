using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Migration.Iri;
using Testcontainers.PostgreSql;

namespace OnToPilot.IntegrationTests.Migration;

/// <summary>
/// Integration tests for <see cref="IriSqlVerifier"/>. The verifier
/// runs ten <c>SELECT COUNT(*) WHERE col LIKE 'old%'</c> + ten
/// <c>SELECT COUNT(*)</c> queries against the same column set
/// <see cref="IriSqlMigrator"/> rewrites, so the test container must
/// use the same EF Core schema as production.
/// <list type="bullet">
///   <item>Each test seeds a fresh <see cref="PostgreSqlContainer"/>,
///   applies the EF Core schema with <c>MigrateAsync</c>, and inserts
///   fixture rows that carry either legacy-prefix or
///   already-rewritten IRIs.</item>
///   <item>The verifier's residual count is asserted to be zero on a
///   clean (post-migration) DB, non-zero on an untouched DB, and the
///   exception message is asserted to enumerate every failing
///   column.</item>
///   <item><see cref="IriSqlVerifyReport.WriteAsync"/> is asserted to
///   produce parseable JSON whose fields round-trip through
///   <see cref="JsonSerializer"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Tests skip silently when docker is unavailable (Windows container
/// without a docker daemon, sandboxed CI runner). The skip pattern
/// mirrors <see cref="IriSqlMigratorTests"/> and
/// <see cref="BlobMigrationTests"/> so the integration test baseline
/// never regresses to "DockerException everywhere".
/// </remarks>
[Trait("Category", "Migration")]
public sealed class IriSqlVerifierTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ontopilot_iri_verify")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        try
        {
            await _container.StartAsync();
            _dockerAvailable = true;
        }
        catch (Exception ex) when (
            ex is Docker.DotNet.DockerApiException
            || ex is System.Net.Http.HttpRequestException
            || ex is TimeoutException
            || ex is InvalidOperationException)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_dockerAvailable)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Return <c>true</c> when docker is unavailable so the calling
    /// <c>[Fact]</c> can early-return; <c>false</c> when the test
    /// should proceed normally.
    /// </summary>
    private bool DockerRequired()
    {
        if (_dockerAvailable) return false;
        return true;
    }

    private OnToPilotDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new OnToPilotDbContext(options);
    }

    /// <summary>
    /// Apply the EF Core schema and (optionally) seed a single
    /// KnowledgeSystem row. The two <c>GraphIri</c> / <c>BaseIri</c>
    /// columns are the ones the verifier's smoke-check actually
    /// asserts on; the other eight ColumnsToRewrite tuples map to
    /// tables whose schema is also materialised by MigrateAsync, so
    /// we get full coverage of the verifier's loop body.
    /// </summary>
    private async Task SeedAsync(string? graphIri, string? baseIri)
    {
        await using var db = BuildContext();
        await db.Database.MigrateAsync();

        if (graphIri is not null && baseIri is not null)
        {
            var ks = new KnowledgeSystemEntity
            {
                PublicId = "test-ks",
                Name = "test",
                Description = "iri-verifier fixture",
                GraphIri = graphIri,
                BaseIri = baseIri,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeSystems.Add(ks);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task VerifyAsync_passes_when_no_residual_rows()
    {
        // 这是 smoke-check 的目标状态:migrator 跑完后,DB 里已经
        // 不含 legacy prefix。verify 应当返回 ResidualTotal == 0。
        if (DockerRequired()) return;
        await SeedAsync(
            graphIri: "http://goodcrew.local/ks/1",
            baseIri:  "http://goodcrew.local/ks/1/onto#");

        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);
        var report = await verifier.VerifyAsync(
            new IriSqlVerifyOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix:   "http://goodcrew.local/"),
            CancellationToken.None);

        Assert.Equal(0L, report.ResidualTotal);
        Assert.NotEmpty(report.Steps);
        Assert.Empty(report.FailingSteps);
        Assert.All(report.Steps, step => Assert.Equal(0L, step.ResidualOldPrefixRows));
    }

    [Fact]
    public async Task VerifyAsync_throws_when_column_has_residual_rows()
    {
        // 这是 smoke-check 抓 false-positive 的关键场景:migrator 没
        // 跑过(或者跑了但失败),DB 里仍然含 legacy prefix,verify
        // 必须抛错。
        if (DockerRequired()) return;
        await SeedAsync(
            graphIri: "http://ontopilot.local/ks/1",
            baseIri:  "http://ontopilot.local/ks/1/onto#");

        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);

        var ex = await Assert.ThrowsAsync<IriSqlVerificationException>(() =>
            verifier.VerifyAsync(
                new IriSqlVerifyOptions(
                    FromPrefix: "http://ontopilot.local/",
                    ToPrefix:   "http://goodcrew.local/"),
                CancellationToken.None));

        // 至少要列出 knowledgesystem.GraphIri 与 knowledgesystem.BaseIri
        // 两个 residual 列(我们 seed 的两个 IRI 列都含 legacy prefix)。
        Assert.Contains(ex.Failures, f =>
            f.Contains("knowledgesystem.GraphIri") && f.Contains("still contain"));
        Assert.Contains(ex.Failures, f =>
            f.Contains("knowledgesystem.BaseIri") && f.Contains("still contain"));
    }

    [Fact]
    public async Task VerifyAsync_reports_table_total_for_empty_column()
    {
        // 空表场景:residual = 0(因为没行),但 TableTotalRows = 0
        // 也应当被记录,让操作员区分"列干净"与"列不存在数据"。
        if (DockerRequired()) return;
        await SeedAsync(graphIri: null, baseIri: null);

        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);
        var report = await verifier.VerifyAsync(
            new IriSqlVerifyOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix:   "http://goodcrew.local/"),
            CancellationToken.None);

        // knowledgesystem 列是空表;Residual=0 且 Total=0。
        var graphStep = report.Steps.Single(s =>
            s.Table == "knowledgesystem" && s.Column == "GraphIri");
        Assert.Equal(0L, graphStep.ResidualOldPrefixRows);
        Assert.Equal(0L, graphStep.TableTotalRows);

        // 整体通过(空列 vacuous pass)。
        Assert.Equal(0L, report.ResidualTotal);
    }

    [Fact]
    public async Task VerifyAsync_aggregates_multiple_failures()
    {
        // 同一个 fixture 同时含 GraphIri 和 BaseIri 两条 legacy 行,
        // 异常 message 应当列出两列(不是 fix-one-run-fail-fix-one)。
        if (DockerRequired()) return;
        await SeedAsync(
            graphIri: "http://ontopilot.local/ks/1",
            baseIri:  "http://ontopilot.local/ks/1/onto#");

        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);

        var ex = await Assert.ThrowsAsync<IriSqlVerificationException>(() =>
            verifier.VerifyAsync(
                new IriSqlVerifyOptions(
                    FromPrefix: "http://ontopilot.local/",
                    ToPrefix:   "http://goodcrew.local/"),
                CancellationToken.None));

        // 与 Assert-AllMigrationManifests 的失败聚合哲学对齐:一
        // 次 throw 列出所有失败列,避免 fix-one-run-fail-fix-one。
        Assert.True(ex.Failures.Count >= 2,
            $"expected ≥2 failures, got {ex.Failures.Count}: {string.Join(" | ", ex.Failures)}");
        Assert.Contains("knowledgesystem.GraphIri", ex.Message);
        Assert.Contains("knowledgesystem.BaseIri", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_throws_when_from_prefix_lacks_path_separator()
    {
        // 与 IriSqlMigrator 对齐:FromPrefix 必须以 / 或 # 结尾,
        // 防止子串误匹配。不需要 docker,纯参数校验。
        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => verifier.VerifyAsync(
            new IriSqlVerifyOptions(
                FromPrefix: "http://ontopilot.local",  // no trailing / or #
                ToPrefix:   "http://goodcrew.local/"),
            CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_writes_valid_json_that_round_trips()
    {
        // 报告写盘是审计 trail 的关键路径:JSON 必须合法,字段必
        // 须可反序列化,这样 cutover record 可以按 SHA-256 引用。
        if (DockerRequired()) return;
        await SeedAsync(
            graphIri: "http://goodcrew.local/ks/1",
            baseIri:  "http://goodcrew.local/ks/1/onto#");

        await using var db = BuildContext();
        var verifier = new IriSqlVerifier(db, NullLogger<IriSqlVerifier>.Instance);
        var report = await verifier.VerifyAsync(
            new IriSqlVerifyOptions(
                FromPrefix: "http://ontopilot.local/",
                ToPrefix:   "http://goodcrew.local/"),
            CancellationToken.None);

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"iri-verify-report-{Guid.NewGuid():N}.json");
        try
        {
            await report.WriteAsync(tempPath, CancellationToken.None);

            Assert.True(File.Exists(tempPath));
            var json = await File.ReadAllTextAsync(tempPath);

            var roundTripped = JsonSerializer.Deserialize<IriSqlVerifyReport>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(roundTripped);
            Assert.Equal(0L, roundTripped!.ResidualTotal);
            Assert.Equal(report.Steps.Count, roundTripped.Steps.Count);

            // SHA-256 必须是 64-char lowercase hex;作为 cutover record
            // 引用审计 trail 的稳定 handle。
            var sha = IriSqlVerifyReport.ComputeReportSha256(tempPath);
            Assert.Equal(64, sha.Length);
            Assert.Matches("^[0-9a-f]{64}$", sha);

            // 同样输入 → 同样 SHA(幂等)。
            var sha2 = IriSqlVerifyReport.ComputeReportSha256(tempPath);
            Assert.Equal(sha, sha2);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
