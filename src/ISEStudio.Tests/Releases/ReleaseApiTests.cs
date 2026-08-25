using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Releases;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/releases*</c>.
/// Mirrors <see cref="ISEStudio.Tests.Conflicts.ConflictApiTests"/>:
/// real Kestrel via <see cref="AuthTestWebApplicationFactory"/>, SQLite
/// per-test database, admin user seeded in-line, raw <c>HttpClient</c>.
///
/// <para>The dispatcher arm <c>releases.create</c> was previously a
/// Stage-1 placeholder returning <c>{id: Guid.Empty, ...}</c> without ever
/// touching the database. The frontend
/// <c>ReleasePanel.createDraft</c> calls <c>api.createRelease(ksId)</c>
/// then immediately <c>load()</c>s the list — so the placeholder return
/// manifested as "success toast, but the list stays empty". The test
/// below pins the new contract: a <c>POST /api/knowledge/{id}/releases</c>
/// call MUST result in a persisted <see cref="OntologyReleaseEntity"/>
/// row plus a wire response that matches the Python
/// <c>_release_out</c> shape so the frontend list can re-render
/// without a second round-trip.</para>
/// </summary>
public sealed class ReleaseApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task CreateDraft_persists_db_row_and_returns_release_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "create-draft");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/releases",
            new { title = "Q4 release", notes = "Initial draft for review." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Wire-shape: must match the Python _release_out() contract so
        // the frontend ReleasePanel can render the row directly without
        // a second fetch. The dispatcher arm previously returned a
        // placeholder with id=Guid.Empty / status="draft" — these
        // assertions catch that regression.
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal(ks.Id, body.GetProperty("knowledge_system_id").GetGuid());
        Assert.Equal("draft", body.GetProperty("status").GetString());
        Assert.Equal("Q4 release", body.GetProperty("title").GetString());
        Assert.Equal("Initial draft for review.", body.GetProperty("notes").GetString());

        // version follows the Python convention "draft-<id>".
        var version = body.GetProperty("version").GetString();
        Assert.NotNull(version);
        Assert.StartsWith("draft-", version);

        // manifest.capture_status="ready" — capture runs synchronously in
        // the request scope (MVP simplification; the Task.Run +
        // IDbContextFactory background path was deferred to a hardening
        // pass because it silently swallowed exceptions). By the time the
        // wire response returns, the three RDF layers are sharded under
        // the artifact root and the row manifest is final. Python's
        // background capture marks "pending" until the job completes;
        // .NET surfaces the same finality on the response so the
        // frontend's load() after createDraft always sees ready.
        var manifest = body.GetProperty("manifest");
        Assert.Equal("ready", manifest.GetProperty("capture_status").GetString());

        // DB-side: the row must actually exist so a subsequent GET
        // /releases (the load() the frontend fires after createDraft)
        // returns it instead of an empty list.
        using (var verifyScope = app.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var rows = db.OntologyReleases
                .Where(r => r.KnowledgeSystemId == ks.Id)
                .ToList();
            Assert.Single(rows);
            var row = rows[0];
            Assert.Equal("draft", row.Status);
            Assert.Equal("Q4 release", row.Title);
            Assert.Equal("Initial draft for review.", row.Notes);
            Assert.StartsWith("draft-", row.Version);
        }
    }

    [Fact]
    public async Task CreateDraft_with_empty_body_object_uses_empty_title_and_notes()
    {
        // The frontend ReleasePanel.createDraft (frontend/src/lib/api.ts:134)
        // always sends `{}` (an empty JSON object) — matches Python
        // defaults of title="" / notes="".
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "create-empty");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/releases", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("", body.GetProperty("title").GetString());
        Assert.Equal("", body.GetProperty("notes").GetString());
        Assert.Equal("draft", body.GetProperty("status").GetString());
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            return;
        }
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(AuthTestWebApplicationFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        return client;
    }

    private static async Task<KnowledgeSystemEntity> SeedKnowledgeSystemAsync(
        AuthTestWebApplicationFactory app, string tag)
    {
        var db = app.CreateDbContext();
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = $"release-tests-{tag}",
            Description = "Seed KS for release contract tests.",
            GraphIri = $"http://goodcrew.local/ks/{tag}",
            BaseIri = $"http://goodcrew.local/ks/{tag}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }
}