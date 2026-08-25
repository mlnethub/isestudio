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
/// HTTP-level contract tests for the B7b slice of the ABox API:
/// <c>abox.add_assertion</c> + <c>abox.remove_assertion</c>. Reuses the
/// scaffolding from <see cref="ABoxApiTests"/> (admin login, KS create,
/// TBox seeding, individual mint) so the assertion tests stay focused on
/// the assertion-specific surface: object / data kinds, FactKey canonical
/// keys in the provenance table, audit capture, role gate, and the
/// extraction 409 envelope.
/// </summary>
public sealed class ABoxAssertionApiTests
{
    private const string CookieHeader = "isestudio_session";

    // -----------------------------------------------------------------
    // Object assertions
    // -----------------------------------------------------------------

    [Fact]
    public async Task AddObjectAssertion_writes_triple_and_provenance()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-obj-add");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var ownsProp = await AddTBoxObjectPropertyAsync(client, ksId, "owns");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);

        var response = await PostAssertionAsync(client, ksId, new
        {
            subject = rexIri,
            prop = ownsProp,
            kind = "object",
            target = aliceIri,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The response is the full individual envelope for the subject.
        Assert.Equal(rexIri, body.GetProperty("iri").GetString());
        var objectAssertions = body.GetProperty("object_assertions");
        Assert.Equal(1, objectAssertions.GetArrayLength());
        Assert.Equal(aliceIri, objectAssertions[0].GetProperty("target").GetString());

        // The ABox graph has the new object-property triple.
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Single(store.Match(
            subjectIri: rexIri,
            predicateIri: ownsProp,
            objectIri: aliceIri,
            graphIri: aboxGraph));

        // The provenance table carries the obj|<sub>|<prop>|<target> key
        // linked back to the audit row so history replay can pivot.
        var provenance = LookupProvenanceRows(app, ksId);
        var factKey = FactKey.ObjectKey(rexIri, ownsProp, aliceIri);
        var row = provenance.Single(p => p.FactKey == factKey);
        Assert.Equal("manual", row.Method);
        Assert.NotNull(row.AuditEventId);

        // The audit row exists with the expected action.
        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "abox.add_assertion").ToList();
        Assert.Single(audits);
        Assert.Equal(aboxGraph, audits[0].Graph);
        Assert.NotNull(audits[0].Added);
    }

    [Fact]
    public async Task RemoveObjectAssertion_strips_triple_and_provenance()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-obj-rm");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var ownsProp = await AddTBoxObjectPropertyAsync(client, ksId, "owns");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);

        // Seed an assertion so we can verify removal wipes both layers.
        await PostAssertionAsync(client, ksId, new
        {
            subject = rexIri,
            prop = ownsProp,
            kind = "object",
            target = aliceIri,
        });
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Single(store.Match(
            subjectIri: rexIri, predicateIri: ownsProp, objectIri: aliceIri,
            graphIri: aboxGraph));

        var response = await PostAssertionDeleteAsync(client, ksId, new
        {
            subject = rexIri,
            prop = ownsProp,
            kind = "object",
            target = aliceIri,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(store.Match(
            subjectIri: rexIri, predicateIri: ownsProp, objectIri: aliceIri,
            graphIri: aboxGraph));

        var provenance = LookupProvenanceRows(app, ksId);
        var factKey = FactKey.ObjectKey(rexIri, ownsProp, aliceIri);
        Assert.DoesNotContain(provenance, p => p.FactKey == factKey);

        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "abox.remove_assertion").ToList();
        Assert.Single(audits);
        Assert.NotNull(audits[0].Removed);
    }

    // -----------------------------------------------------------------
    // Data assertions
    // -----------------------------------------------------------------

    [Fact]
    public async Task AddDataAssertion_writes_literal_and_provenance()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-data");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ageProp = await AddTBoxDataPropertyAsync(client, ksId, "age");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);

        var response = await PostAssertionAsync(client, ksId, new
        {
            subject = rexIri,
            prop = ageProp,
            kind = "data",
            value = "5",
            datatype = "http://www.w3.org/2001/XMLSchema#integer",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var dataAssertions = body.GetProperty("data_assertions");
        Assert.Equal(1, dataAssertions.GetArrayLength());
        Assert.Equal("5", dataAssertions[0].GetProperty("value").GetString());

        var provenance = LookupProvenanceRows(app, ksId);
        var factKey = FactKey.DataKey(rexIri, ageProp, "5");
        Assert.Single(provenance, p => p.FactKey == factKey);
    }

    // -----------------------------------------------------------------
    // Validation + role gate + idempotency
    // -----------------------------------------------------------------

    [Fact]
    public async Task AddAssertion_with_unknown_property_returns_500()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-bad-prop");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        var before = store.Match(graphIri: aboxGraph).Count;

        var response = await PostAssertionAsync(client, ksId, new
        {
            subject = rexIri,
            prop = "http://example.com/no-such-prop",
            kind = "object",
            target = aliceIri,
        });
        // InvalidOperationException("Unknown property") surfaces as 500.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.Equal(before, store.Match(graphIri: aboxGraph).Count);
        Assert.Empty(LookupProvenanceRows(app, ksId));
    }

    [Fact]
    public async Task AddObjectAssertion_without_editor_role_returns_empty_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-norole");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var ownsProp = await AddTBoxObjectPropertyAsync(client, ksId, "owns");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);
        var (aliceClient, _) = await SeedSecondUserAsync(app);

        var response = await aliceClient.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/assertions",
            new
            {
                subject = rexIri,
                prop = ownsProp,
                kind = "object",
                target = aliceIri,
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(string.Empty, body.GetProperty("iri").GetString());

        // The RDF graph + provenance table stay clean.
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Empty(store.Match(predicateIri: ownsProp, graphIri: aboxGraph));
        Assert.Empty(LookupProvenanceRows(app, ksId));
    }

    [Fact]
    public async Task AddObjectAssertion_is_idempotent_on_duplicate()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "abox-assert-idemp");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var ownsProp = await AddTBoxObjectPropertyAsync(client, ksId, "owns");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);
        var body = new
        {
            subject = rexIri,
            prop = ownsProp,
            kind = "object",
            target = aliceIri,
        };

        var first = await PostAssertionAsync(client, ksId, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAssertionAsync(client, ksId, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksId);
        Assert.Single(store.Match(
            subjectIri: rexIri, predicateIri: ownsProp, objectIri: aliceIri,
            graphIri: aboxGraph));

        // The provenance row is upserted (single row, most-recent audit id),
        // not duplicated — same shape as Python's record_abox_fact upsert.
        var provenance = LookupProvenanceRows(app, ksId);
        var factKey = FactKey.ObjectKey(rexIri, ownsProp, aliceIri);
        Assert.Single(provenance, p => p.FactKey == factKey);
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
                LegacyId = TestLegacyIds.Next("users"),
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
                LegacyId = TestLegacyIds.Next("users"),
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

    private static IReadOnlyList<AboxProvenanceEntity> LookupProvenanceRows(
        AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.AboxProvenances.AsNoTracking()
            .Where(p => p.KnowledgeSystemId == ksId)
            .ToList();
    }

    private static async Task<string> AddTBoxClassAsync(
        HttpClient client, Guid ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> AddTBoxObjectPropertyAsync(
        HttpClient client, Guid ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_property", kind = "object", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> AddTBoxDataPropertyAsync(
        HttpClient client, Guid ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_property", kind = "data", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

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

    private static async Task<HttpResponseMessage> PostAssertionAsync(
        HttpClient client, Guid ksId, object body)
    {
        return await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/assertions", body);
    }

    private static async Task<HttpResponseMessage> PostAssertionDeleteAsync(
        HttpClient client, Guid ksId, object body)
    {
        return await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/assertions/delete", body);
    }
}