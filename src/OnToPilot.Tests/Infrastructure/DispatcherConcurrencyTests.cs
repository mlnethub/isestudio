using System.Net;
using System.Net.Http.Json;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Infrastructure;

/// <summary>
/// Regression test for the <c>InternalOperationDispatcher</c>
/// captive-dependency bug. The dispatcher used to be registered as a
/// Singleton, so its constructor captured the root <see cref="IServiceProvider"/>;
/// every concurrent request that resolved a scoped service
/// (<c>KnowledgeService</c>, <c>ConflictService</c>, <c>DocumentService</c>,
/// <c>OnToPilotDbContext</c>) ended up sharing one <c>DbContext</c>
/// instance across all requests. EF Core then threw
/// <c>InvalidOperationException: A second operation was started on this
/// context instance before a previous operation completed</c> as soon as
/// two requests ran concurrently.
///
/// Once the dispatcher is registered Scoped, every request gets its own
/// dispatcher + its own <c>DbContext</c>, and the burst below passes.
///
/// Mirrors the <c>Task.Run</c> + <c>Task.WhenAll</c> house pattern used by
/// <c>ReleaseManagerTests.Concurrent_workspace_writes_do_not_leak_into_published_view</c>
/// and the single-GET shape of
/// <c>ConflictApiTests.List_returns_empty_when_no_conflicts</c> +
/// <c>KnowledgeApiTests.ReviewCounts_returns_four_buckets_and_total</c>.
/// </summary>
public sealed class DispatcherConcurrencyTests
{
    private const string CookieHeader = "ontopilot_session";

    [Fact]
    public async Task Concurrent_dispatcher_requests_do_not_share_DbContext()
    {
        const int parallelism = 8;
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "concurrent-burst");

        // Two distinct dispatcher paths so EF Core actually starts a
        // second operation on the shared DbContext (not just retries the
        // same query). Both go through InternalOperationDispatcher ->
        // KnowledgeService / ConflictService -> OnToPilotDbContext, which
        // is exactly the captive path that used to throw.
        var urls = Enumerable.Range(0, parallelism / 2)
                .Select(_ => $"/api/knowledge/{ksId}/conflicts?status=all")
                .Concat(Enumerable.Range(0, parallelism / 2)
                    .Select(_ => $"/api/knowledge/{ksId}/review/counts"))
                .ToArray();

        var tasks = urls
            .Select(url => Task.Run(async () =>
            {
                var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                return new { Url = url, Status = (int)response.StatusCode, Body = body };
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            Assert.True(
                r.Status == (int)HttpStatusCode.OK,
                $"Request to {r.Url} returned {(HttpStatusCode)r.Status}: {r.Body}");

            Assert.DoesNotContain(
                "A second operation was started",
                r.Body,
                StringComparison.Ordinal);

            // Belt-and-braces: ensure the dispatcher actually ran the
            // per-call resolve path (returning a real payload) rather
            // than a cached null / placeholder.
            Assert.NotEqual("null", r.Body);
        }
    }

    // ---- helpers ----------------------------------------------------------
    // Inlined from KnowledgeApiTests.SeedAdminAndClientAsync so this test
    // is self-contained and the failure mode is visible alongside the
    // assertions.

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                    AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
                IsAdmin = true,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var adminId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    /// <summary>POST a KS and return its wire primary-key <see cref="Guid"/>.</summary>
    private static async Task<Guid> CreateKsAsync(HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
