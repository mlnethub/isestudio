using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Authentication;
using OnToPilot.ApiContract.Tests.Baseline;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.ApiContract.Tests;

/// <summary>
/// Contract tests for the published release surface
/// (<c>/api/v1/knowledge-systems/{public_id}/published/*</c> and
/// <c>/releases/{version}/*</c>). The brief mandates the cache-header
/// shape and the lifecycle (provisioning / stopped) status codes:
/// <list type="bullet">
///   <item>Pinned version &rarr; <c>Cache-Control: private, max-age=31536000, immutable</c>.</item>
///   <item>Current release &rarr; <c>Cache-Control: private, no-cache</c>.</item>
///   <item>Always &rarr; <c>X-OntoPilot-Release</c> + a quoted
///         <c>ETag</c> derived from the release manifest.</item>
///   <item>Deployment <c>provisioning</c> &rarr; HTTP 503 + <c>Retry-After: 2</c>.</item>
///   <item>Deployment <c>stopped</c> / <c>failed</c> &rarr; HTTP 410.</item>
/// </list>
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class PublishedCacheContractTests
{
    private const string PublicId = "test-ks";

    [Fact]
    public async Task Current_release_sets_release_and_etag_headers_with_no_cache()
    {
        var plaintext = await SeedAsync(deploymentStatus: "active", releaseVersion: "v1");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/published");
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Cache header shape mandated by the brief. We assert the
        // parsed CacheControl directives because the ASP.NET Core
        // header writer normalises the order
        // (max-age, private, immutable), so an exact string compare
        // against the brief's "private, max-age=..., immutable" is
        // not the right test.
        Assert.Equal("v1", response.Headers.GetValues("X-OntoPilot-Release").Single());
        Assert.Equal("\"vv1\"", response.Headers.ETag?.Tag);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
    }

    [Fact]
    public async Task Pinned_release_sets_immutable_cache_control_header()
    {
        var plaintext = await SeedAsync(deploymentStatus: "active", releaseVersion: "v2");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/releases/v2");
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("v2", response.Headers.GetValues("X-OntoPilot-Release").Single());
        Assert.Equal("\"vv2\"", response.Headers.ETag?.Tag);
        // The brief mandates the directives — assert them as parsed
        // values because the framework normalises the on-the-wire
        // directive order.
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.MaxAge.HasValue);
        Assert.Equal(TimeSpan.FromSeconds(31_536_000), response.Headers.CacheControl.MaxAge!.Value);
        var extensions = new HashSet<string>(
            response.Headers.CacheControl.Extensions.Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains("immutable", extensions);
    }

    [Fact]
    public async Task Provisioning_deployment_returns_503_with_retry_after()
    {
        var plaintext = await SeedAsync(deploymentStatus: "provisioning", releaseVersion: "v1");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/published");
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("2", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString()
            ?? response.Headers.GetValues("Retry-After").Single());
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("failed")]
    public async Task Stopped_or_failed_deployment_returns_410(string deploymentStatus)
    {
        var plaintext = await SeedAsync(deploymentStatus: deploymentStatus, releaseVersion: "v1");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/published");
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Pinned_release_returns_410_when_release_marked_deleted()
    {
        var plaintext = await SeedAsync(
            deploymentStatus: "active",
            releaseVersion: "v9",
            releaseStatus: "deleted");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/releases/v9");
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Current_release_returns_401_when_token_missing()
    {
        // Seed the row so the request reaches the auth handler.
        await SeedAsync(deploymentStatus: "active", releaseVersion: "v1");

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/knowledge-systems/{PublicId}/published");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(response.Headers.WwwAuthenticate);
    }

    // ---- helpers ----

    /// <summary>
    /// Build a <see cref="HttpClient"/> against the live test host so
    /// the calling test can issue a real HTTP request through the
    /// auth / routing / controller pipeline. Reuses
    /// <see cref="ApiContractWebApplicationFactory"/> so the data the
    /// seed phase wrote to its sqlite file is the data the request
    /// phase reads.
    /// </summary>
    private static HttpClient CreateClient() => SharedFactory.Instance.CreateClient();

    /// <summary>
    /// Singleton factory used by every published-cache test so the
    /// seed phase and the request phase share the same per-run
    /// sqlite file. The first call boots the host; subsequent calls
    /// reuse it.
    /// </summary>
    private sealed class SharedFactory : IDisposable
    {
        public static readonly SharedFactory Instance = new();
        public readonly ApiContractWebApplicationFactory _factory = new();
        public HttpClient CreateClient() => _factory.CreateClient();
        public void Dispose() => _factory.Dispose();
    }

    /// <summary>
    /// Seed a fresh knowledge system + a published release + a
    /// deployment with the supplied lifecycle status, plus a token
    /// bound to the KS. The KS / release upsert is idempotent
    /// (matches on <c>PublicId</c> / <c>(KS, Version)</c>) so the
    /// shared factory's sqlite file does not collide on its UNIQUE
    /// constraints between consecutive tests.
    /// </summary>
    private static async Task<string> SeedAsync(
        string deploymentStatus,
        string releaseVersion,
        string releaseStatus = "published")
    {
        using var scope = SharedFactory.Instance._factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        await db.Database.EnsureCreatedAsync();

        var ks = await db.KnowledgeSystems.FirstOrDefaultAsync(x => x.PublicId == PublicId);
        if (ks is null)
        {
            ks = new KnowledgeSystemEntity
            {
                LegacyId = await NextLegacyIdAsync(db, table: "knowledgesystem"),
                PublicId = PublicId,
                Name = PublicId,
                Description = string.Empty,
                GraphIri = $"http://test/{PublicId}",
                BaseIri = $"http://test/{PublicId}#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeSystems.Add(ks);
            await db.SaveChangesAsync();
        }

        var release = await db.OntologyReleases.FirstOrDefaultAsync(
            r => r.KnowledgeSystemId == ks.Id && r.Version == releaseVersion);
        if (release is null)
        {
            release = new OntologyReleaseEntity
            {
                LegacyId = await NextLegacyIdAsync(db, table: "ontologyrelease"),
                KnowledgeSystemId = ks.Id,
                Version = releaseVersion,
                Status = releaseStatus,
                Title = releaseVersion,
                Notes = string.Empty,
                SnapshotDir = string.Empty,
                Manifest = null,
                CreatedByName = string.Empty,
                ReviewedByName = string.Empty,
                PublishedByName = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.OntologyReleases.Add(release);
            await db.SaveChangesAsync();
        }
        else
        {
            // Update the status (e.g. for the "deleted" pinned-release
            // case) so the test runs the path it claims to exercise.
            release.Status = releaseStatus;
            await db.SaveChangesAsync();
        }

        // Drop any old deployment for this KS so the latest
        // SeedAsync call's deployment status is the one the request
        // phase sees. A clean teardown avoids the
        // "ordering-by-created-at returns a previous run's row" bug.
        var existingDeployments = await db.ReleaseDeployments
            .Where(d => d.KnowledgeSystemId == ks.Id)
            .ToListAsync();
        if (existingDeployments.Count > 0)
        {
            db.ReleaseDeployments.RemoveRange(existingDeployments);
            await db.SaveChangesAsync();
        }

        var deployment = new ReleaseDeploymentEntity
        {
            LegacyId = await NextLegacyIdAsync(db, table: "releasedeployment"),
            KnowledgeSystemId = ks.Id,
            ReleaseId = release.Id,
            Status = deploymentStatus,
            TboxGraphIri = string.Empty,
            VocabularyGraphIri = string.Empty,
            AboxGraphIri = string.Empty,
            StatementCount = 0,
            ProvenanceCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ReleaseDeployments.Add(deployment);
        await db.SaveChangesAsync();

        var plaintext = Guid.NewGuid().ToString("N");
        var token = new KnowledgeApiTokenEntity
        {
            LegacyId = await NextLegacyIdAsync(db, table: "knowledgeapitoken"),
            KnowledgeSystemId = ks.Id,
            Name = "test-token",
            TokenPrefix = plaintext[..Math.Min(16, plaintext.Length)],
            TokenHash = KnowledgeApiTokenService.Digest(plaintext),
            Scopes = new List<string>
            {
                KnowledgeApiTokenScopes.OntologyRead,
                KnowledgeApiTokenScopes.VocabularyRead,
                KnowledgeApiTokenScopes.InstancesRead,
                KnowledgeApiTokenScopes.QueryRead,
            },
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeApiTokens.Add(token);
        await db.SaveChangesAsync();
        return plaintext;
    }

    /// <summary>
    /// Allocate the next <c>legacy_id</c> for a table by selecting the
    /// current MAX + 1. The EF Core schema declares a unique index on
    /// every <c>legacy_id</c> column, so two test runs sharing the
    /// same sqlite file (via the singleton factory) cannot blindly
    /// hard-code 0/1/2.
    /// </summary>
    private static async Task<long> NextLegacyIdAsync(OnToPilotDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(legacy_id), 0) + 1 FROM {table}";
        var raw = await cmd.ExecuteScalarAsync();
        return raw is long l ? l : Convert.ToInt64(raw ?? 1L);
    }
}
