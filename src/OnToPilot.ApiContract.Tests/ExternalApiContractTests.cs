using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.ApiContract.Tests.Baseline;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.ApiContract.Tests;

/// <summary>
/// Contract tests for the external read-only API
/// (<c>/api/v1/knowledge-systems/{public_id}/*</c>) introduced in
/// stage 4 task 3. The four required cases the brief calls out are:
/// <list type="number">
///   <item><c>External_query_rejects_non_read_only_sparql</c> &mdash; the
///         <see cref="OnToPilot.Api.ReadOnlySparqlPolicy"/> reject rules
///         must surface HTTP 400 for INSERT and SERVICE.</item>
///   <item><c>401_with_www_authenticate</c> &mdash; missing / invalid
///         token must produce 401 with the RFC 6750 challenge header.</item>
///   <item><c>403_for_insufficient_scope</c> &mdash; a valid token
///         missing the required scope surfaces 403.</item>
///   <item>Provisioning / stopped / failed lifecycle is covered in
///         <see cref="PublishedCacheContractTests"/>; the test matrix
///         for that surface lives next to its routes.</item>
/// </list>
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class ExternalApiContractTests
{
    private const string PublicId = "test-ks";

    [Theory]
    [InlineData("INSERT DATA { <a> <b> <c> }", HttpStatusCode.BadRequest)]
    [InlineData("SELECT * WHERE { SERVICE <https://example.test> { ?s ?p ?o } }", HttpStatusCode.BadRequest)]
    public async Task External_query_rejects_non_read_only_sparql(string sparql, HttpStatusCode status)
    {
        // External.PostQueryAsync — the brief mandates the verbatim
        // shape of the test method so the inventory hook picks the
        // assertion up by name.
        using var client = SharedFactory.Instance.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/{PublicId}/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = sparql }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {Guid.NewGuid():N}");

        var response = await client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task External_query_returns_401_with_www_authenticate_when_token_missing()
    {
        using var client = SharedFactory.Instance.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/{PublicId}/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = "SELECT * WHERE { ?s ?p ?o }" }),
                Encoding.UTF8,
                "application/json"),
        };
        // No Authorization header — the handler must challenge with
        // 401 + WWW-Authenticate per RFC 6750.

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(response.Headers.WwwAuthenticate);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task External_query_returns_403_when_scope_missing()
    {
        // The token's KS is configured, but its scope list does not
        // include query:read, so the controller must refuse with 403
        // (after the auth scheme has accepted the bearer).
        var plaintext = await SeedTokenWithScopeAsync("ontology:read");

        using var client = SharedFactory.Instance.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/{PublicId}/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = "SELECT * WHERE { ?s ?p ?o }" }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task External_query_succeeds_with_token_and_scope()
    {
        // Happy-path: token with the right scope + a SELECT query →
        // 200, FastAPI envelope, and the body is the empty rows
        // placeholder the dispatcher returns in the absence of a
        // concrete SPARQL executor (task 4 will swap the
        // placeholder for the real implementation).
        var plaintext = await SeedTokenWithScopeAsync("query:read");

        using var client = SharedFactory.Instance.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/{PublicId}/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = "SELECT * WHERE { ?s ?p ?o }" }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The dispatcher returns { rows: [] } for the read-only query
        // until the Oxigraph executor lands; the contract test only
        // requires the read-only policy to admit a well-formed SELECT
        // and the response to come back 200.
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("rows", out _));
    }

    [Fact]
    public async Task External_query_returns_403_when_token_targets_other_ks()
    {
        // Token is bound to KS A; request targets KS B. The controller
        // must refuse with 403 so a stolen token cannot probe other
        // public-ids.
        var otherPublicId = "other-ks";
        var plaintext = await SeedTokenWithScopeAsync("query:read", publicId: "ks-a");

        using var client = SharedFactory.Instance.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/{otherPublicId}/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = "SELECT * WHERE { ?s ?p ?o }" }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {plaintext}");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- helpers ----

    /// <summary>
    /// Singleton factory used by every external-contract test so the
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
    /// Insert a fresh <see cref="KnowledgeSystemEntity"/> + matching
    /// <see cref="KnowledgeApiTokenEntity"/> and return the bearer
    /// plaintext the test should send in the
    /// <c>Authorization: Bearer ...</c> header. Uses the shared test
    /// factory's sqlite database so the EF Core / ASP.NET Core
    /// pipeline sees the same row the real handler will load. The KS
    /// upsert is idempotent (matches by <c>PublicId</c>) so the
    /// shared factory's sqlite file does not collide on its UNIQUE
    /// constraint between consecutive tests.
    /// </summary>
    private static async Task<string> SeedTokenWithScopeAsync(string scope, string publicId = PublicId)
    {
        using var scope0 = SharedFactory.Instance._factory.Services.CreateScope();
        var db = scope0.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        await db.Database.EnsureCreatedAsync();

        var ks = await db.KnowledgeSystems.FirstOrDefaultAsync(x => x.PublicId == publicId);
        if (ks is null)
        {
            ks = new KnowledgeSystemEntity
            {
                LegacyId = await NextLegacyIdAsync(db, table: "knowledgesystem"),
                PublicId = publicId,
                Name = publicId,
                Description = string.Empty,
                GraphIri = $"http://test/{publicId}",
                BaseIri = $"http://test/{publicId}#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeSystems.Add(ks);
            await db.SaveChangesAsync();
        }

        var plaintext = Guid.NewGuid().ToString("N");
        var token = new KnowledgeApiTokenEntity
        {
            LegacyId = await NextLegacyIdAsync(db, table: "knowledgeapitoken"),
            KnowledgeSystemId = ks.Id,
            Name = "test-token",
            TokenPrefix = plaintext[..Math.Min(16, plaintext.Length)],
            TokenHash = KnowledgeApiTokenService.Digest(plaintext),
            Scopes = new List<string> { scope },
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
