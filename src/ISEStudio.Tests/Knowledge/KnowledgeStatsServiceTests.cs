using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Knowledge;

/// <summary>
/// HTTP-level regression tests for the cached TBox stats surfaced on
/// the home-page knowledge-system cards. Mirrors the Python baseline
/// <c>backend/app/api/knowledge.py::refresh_ks_stats</c>: when the
/// RDF graph gains a new class / property / axiom, the SQL-side
/// <c>ClassCount / PropertyCount / AxiomCount</c> columns must follow
/// &mdash; the home page reads those columns via
/// <c>GET /api/knowledge</c>, and previously the .NET port never
/// refreshed them, leaving the cards stuck on
/// <c>0 类 / 0 属性 / 0 公理</c> even when the graph already had
/// content.
/// </summary>
public sealed class KnowledgeStatsServiceTests
{
    private const string CookieHeader = "isestudio_session";

    // -----------------------------------------------------------------
    // Ontology edit -> cached counts follow automatically
    // -----------------------------------------------------------------

    [Fact]
    public async Task Ontology_edit_refreshes_cached_counts_on_home_card()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "stats-edit");

        // Baseline: a brand-new KS reports 0 / 0 / 0.
        var baseline = await FetchHomeCardAsync(client, ksId);
        Assert.Equal(0, baseline.ClassCount);
        Assert.Equal(0, baseline.PropertyCount);
        Assert.Equal(0, baseline.AxiomCount);

        // Add one class via the ontology.edit endpoint. This is the
        // exact mutation path that Python calls refresh_ks_stats after
        // (backend/app/api/ontology.py:138).
        var editClass = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        Assert.Equal(HttpStatusCode.OK, editClass.StatusCode);

        // Add one object property so PropertyCount moves.
        var editProp = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_property", kind = "object", label = "hasOwner" });
        Assert.Equal(HttpStatusCode.OK, editProp.StatusCode);

        // Add a second class so the subclass axiom has both endpoints
        // pre-existing (add_axiom auto-creates missing classes via
        // EnsureLabeledClass, which would inflate ClassCount). This
        // way the axiom increments only AxiomCount.
        var editDog = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Dog" });
        Assert.Equal(HttpStatusCode.OK, editDog.StatusCode);

        var editAxiom = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_axiom", type = "subclass", sub = "Dog", @super = "Animal" });
        Assert.Equal(HttpStatusCode.OK, editAxiom.StatusCode);

        // After each successful edit the cached columns must reflect
        // the live TBox graph (mirrors Python's per-edit refresh).
        var afterProps = await FetchHomeCardAsync(client, ksId);
        Assert.Equal(2, afterProps.ClassCount);
        Assert.Equal(1, afterProps.PropertyCount);
        Assert.Equal(1, afterProps.AxiomCount);
    }

    // -----------------------------------------------------------------
    // Manual refresh_stats operator path (mcp_server.py:634 parity)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Refresh_stats_recomputes_counts_from_live_graph()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "stats-refresh");

        // Bypass the edit-time refresh by corrupting the cached counts
        // to a known-wrong value, then verify the explicit repair path
        // restores them from the live graph.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ISEStudio.Infrastructure.Persistence.ISEStudioDbContext>();
            var ks = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.KnowledgeSystems, k => k.Id == ksId);
            Assert.NotNull(ks);
            ks!.ClassCount = 999;
            ks.PropertyCount = 999;
            ks.AxiomCount = 999;
            await db.SaveChangesAsync();
        }

        var refresh = await client.PostAsync(
            $"/api/knowledge/{ksId}/refresh_stats", content: null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var body = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("refreshed").GetBoolean());

        var repaired = await FetchHomeCardAsync(client, ksId);
        Assert.Equal(0, repaired.ClassCount);
        Assert.Equal(0, repaired.PropertyCount);
        Assert.Equal(0, repaired.AxiomCount);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<HomeCardCounts> FetchHomeCardAsync(HttpClient client, Guid ksId)
    {
        var list = await client.GetAsync("/api/knowledge");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var item in body.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == ksId)
            {
                return new HomeCardCounts(
                    item.GetProperty("class_count").GetInt32(),
                    item.GetProperty("property_count").GetInt32(),
                    item.GetProperty("axiom_count").GetInt32());
            }
        }
        throw new InvalidOperationException($"KS {ksId} not found in /api/knowledge response.");
    }

    private readonly record struct HomeCardCounts(int ClassCount, int PropertyCount, int AxiomCount);

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
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var adminId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<Guid> CreateKsAsync(HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"KS-{tag}",
            description = "stats test",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }
}
