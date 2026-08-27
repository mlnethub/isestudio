using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// HTTP-level contract tests for the B7a slice of the ABox API:
/// <c>list_classes</c>, <c>list_individuals</c>, <c>get_individual</c>,
/// <c>create_individual</c>, <c>delete_individual</c>. Mirrors the
/// <see cref="OntologyApiTests"/> template: real Kestrel, SQLite +
/// per-test temp roots (blob + rdf), Oxigraph wired via
/// <see cref="AuthTestWebApplicationFactory"/> so
/// <see cref="ABoxService"/> has a writable graph handle.
/// </summary>
public sealed class ABoxApiTests
{
    private const string CookieHeader = "isestudio_session";

    // -----------------------------------------------------------------
    // List / get
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListClasses_returns_empty_when_tbox_has_no_classes()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-classes-empty");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/abox/classes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("classes").GetArrayLength());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ListClasses_annotates_counts_per_class()
    {
        // Seed two TBox classes, then create individuals of each so
        // counts are visible. Sorted by (-count, label) per Python
        // semantics: the more-populated class sorts first.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-classes-counts");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var catClassIri = await AddTBoxClassAsync(client, ksId, "Cat");

        await CreateIndividualAsync(client, ksId, "Rex", dogClassIri);
        await CreateIndividualAsync(client, ksId, "Buddy", dogClassIri);
        await CreateIndividualAsync(client, ksId, "Whiskers", catClassIri);

        var response = await client.GetAsync($"/api/knowledge/{ksId}/abox/classes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var classes = body.GetProperty("classes");
        Assert.Equal(2, classes.GetArrayLength());
        // Sorted by (-count, label): Dog has count 2, Cat has count 1.
        Assert.Equal(dogClassIri, classes[0].GetProperty("iri").GetString());
        Assert.Equal(2, classes[0].GetProperty("count").GetInt32());
        Assert.Equal(catClassIri, classes[1].GetProperty("iri").GetString());
        Assert.Equal(1, classes[1].GetProperty("count").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ListIndividuals_filters_by_class_and_q()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-list");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var catClassIri = await AddTBoxClassAsync(client, ksId, "Cat");
        await CreateIndividualAsync(client, ksId, "Rex", dogClassIri);
        await CreateIndividualAsync(client, ksId, "Buddy", dogClassIri);
        await CreateIndividualAsync(client, ksId, "Whiskers", catClassIri);

        // Filter by class_iri
        var byClass = await client.GetAsync(
            $"/api/knowledge/{ksId}/abox/individuals?class_iri={Uri.EscapeDataString(dogClassIri)}");
        Assert.Equal(HttpStatusCode.OK, byClass.StatusCode);
        var byClassBody = await byClass.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, byClassBody.GetProperty("total").GetInt32());
        Assert.Equal(2, byClassBody.GetProperty("items").GetArrayLength());

        // Each row must carry the type objects the InstancesPanel renders
        // (matches IndividualOut.Types shape on the detail endpoint AND the
        // Python baseline in backend/app/ontology/abox.py::list_individuals).
        var buddy = byClassBody.GetProperty("items")[0];
        var buddyTypes = buddy.GetProperty("types");
        Assert.Equal(JsonValueKind.Array, buddyTypes.ValueKind);
        Assert.Equal(1, buddyTypes.GetArrayLength());
        Assert.Equal(dogClassIri, buddyTypes[0].GetProperty("iri").GetString());
        Assert.Equal("Dog", buddyTypes[0].GetProperty("label").GetString());

        // Filter by q (label substring, case-insensitive)
        var byQ = await client.GetAsync($"/api/knowledge/{ksId}/abox/individuals?q=rex");
        Assert.Equal(HttpStatusCode.OK, byQ.StatusCode);
        var byQBody = await byQ.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, byQBody.GetProperty("total").GetInt32());
        Assert.Equal("Rex", byQBody.GetProperty("items")[0]
            .GetProperty("label").GetString());
    }

    [Fact]
    public async Task GetIndividual_returns_label_and_types()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-get");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var iri = await CreateIndividualAsync(client, ksId, "Rex", dogClassIri);

        var response = await client.GetAsync(
            $"/api/knowledge/{ksId}/abox/individual?iri={Uri.EscapeDataString(iri)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(iri, body.GetProperty("iri").GetString());
        Assert.Equal("Rex", body.GetProperty("label").GetString());
        var types = body.GetProperty("types");
        Assert.Equal(1, types.GetArrayLength());
        Assert.Equal(dogClassIri, types[0].GetProperty("iri").GetString());
        Assert.Equal("Dog", types[0].GetProperty("label").GetString());
    }

    // -----------------------------------------------------------------
    // Mutations
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateIndividual_writes_class_label_and_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-create");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var iri = await CreateIndividualAsync(client, ksId, "Rex", dogClassIri);

        // The ABox graph has the three triples (rdf:type named individual,
        // rdf:type Dog, rdfs:label Rex).
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Single(store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            objectIri: "http://www.w3.org/2002/07/owl#NamedIndividual",
            graphIri: aboxGraph));
        Assert.Single(store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            objectIri: dogClassIri,
            graphIri: aboxGraph));
        Assert.Single(store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/2000/01/rdf-schema#label",
            graphIri: aboxGraph));

        // Audit row carries the action + the N-Quads diff so history
        // replay can roll back the write.
        var audits = LookupAuditEventsFor(app, ksId);
        var createAudit = audits.Single(e => e.Action == "abox.add_individual");
        Assert.Equal(aboxGraph, createAudit.Graph);
        Assert.NotNull(createAudit.Added);
        Assert.Contains("Rex", System.Text.Encoding.UTF8.GetString(createAudit.Added!));
    }

    [Fact]
    public async Task CreateIndividual_with_unknown_class_returns_500_and_no_writes()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-bad-class");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        var before = store.Match(graphIri: aboxGraph).Count;

        // The service throws InvalidOperationException("Unknown class"),
        // which the FastApiErrorMiddleware translates to a 500 with the
        // standard envelope. The graph is untouched because the throw
        // happened inside CaptureAsync (revert-on-error = true).
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/individuals",
            new { label = "Mystery", class_iri = "http://nope/unknown" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var after = store.Match(graphIri: aboxGraph).Count;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DeleteIndividual_removes_all_subject_quads_and_writes_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-delete");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var iri = await CreateIndividualAsync(client, ksId, "Rex", dogClassIri);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.True(store.Match(subjectIri: iri, graphIri: aboxGraph).Count > 0);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/individuals/delete",
            new { iri });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("removed").GetInt32() >= 3);

        Assert.Empty(store.Match(subjectIri: iri, graphIri: aboxGraph));

        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "abox.delete_individual").ToList();
        Assert.Single(audits);
        Assert.NotNull(audits[0].Removed);
        Assert.Contains("Rex", System.Text.Encoding.UTF8.GetString(audits[0].Removed!));
    }

    [Fact]
    public async Task CreateIndividual_without_editor_role_returns_empty_envelope()
    {
        // A non-admin non-grantee user has no role on the KS. The
        // service returns null on role-deny; the dispatcher surfaces
        // the empty IndividualRef fallback so the existing route still
        // 200s without leaking the role state. The graph stays empty.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-norole");

        var dogClassIri = await AddTBoxClassAsync(client, ksId, "Dog");
        var (aliceClient, _) = await SeedSecondUserAsync(app);

        var response = await aliceClient.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/individuals",
            new { label = "Sneaky", class_iri = dogClassIri });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Empty fallback: iri = "" and types = [].
        Assert.Equal(string.Empty, body.GetProperty("iri").GetString());
        Assert.Equal(0, body.GetProperty("types").GetArrayLength());

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Empty(store.Match(graphIri: aboxGraph));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            var passwordService = new ISEStudio.Authentication.PasswordService();
            db.Users.Add(new UserEntity
            {
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = passwordService.Hash(AuthTestWebApplicationFactory.AdminPassword),
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
        var adminId = db.Users
            .Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<(HttpClient Client, Guid UserId)> SeedSecondUserAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.OtherUsername))
        {
            var passwordService = new ISEStudio.Authentication.PasswordService();
            db.Users.Add(new UserEntity
            {
                Username = AuthTestWebApplicationFactory.OtherUsername,
                DisplayName = "Alice",
                PasswordHash = passwordService.Hash(AuthTestWebApplicationFactory.OtherPassword),
                IsAdmin = false,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.OtherUsername,
            password = AuthTestWebApplicationFactory.OtherPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var userId = db.Users
            .Single(u => u.Username == AuthTestWebApplicationFactory.OtherUsername).Id;
        return (client, userId);
    }

    private static async Task<Guid> CreateKsAsync(
        HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The wire `id` is the KS primary-key Guid (the migration removed
        // the legacy integer from the DTO).
        return body.GetProperty("id").GetGuid();
    }

    private static string LookupKsAbboxIri(AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksId)
            .Select(k => k.GraphIri)
            .Single()
            .TrimEnd('/') + "/abox";
    }

    private static IReadOnlyList<AuditEventEntity> LookupAuditEventsFor(
        AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.AuditEvents.AsNoTracking()
            .Where(e => e.KnowledgeSystemId == ksId)
            .ToList();
    }

    /// <summary>
    /// Seed a TBox class via the ontology edit endpoint so the abox
    /// sidebar / types / filter logic has a class to look up. Returns
    /// the minted class IRI.
    /// </summary>
    private static async Task<string> AddTBoxClassAsync(HttpClient client, Guid ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    /// <summary>
    /// Create an individual and return its minted IRI. Convenience
    /// wrapper that asserts the response shape so the caller doesn't
    /// repeat the boilerplate.
    /// </summary>
    private static async Task<string> CreateIndividualAsync(
        HttpClient client, Guid ksId, string label, string classIri)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/individuals",
            new { label, class_iri = classIri });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var iri = body.GetProperty("iri").GetString();
        Assert.False(string.IsNullOrEmpty(iri));
        return iri!;
    }
}