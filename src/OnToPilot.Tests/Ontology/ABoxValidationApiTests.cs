using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// HTTP-level contract tests for the B7c slice of the ABox API:
/// <c>abox.reset</c> + <c>abox.validate</c> + <c>abox.fix_violation</c> +
/// <c>validation/decisions</c> list + revoke. Reuses the scaffolding from
/// <see cref="ABoxAssertionApiTests"/> so the validation tests stay focused
/// on the validation-specific behaviour: violation envelope shape, fix-op
/// dispatch, the <c>relax_range</c> side-effect (TBox edit + decision row),
/// and the reset wipe (RDF + provenance + audit).
/// </summary>
public sealed class ABoxValidationApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // -----------------------------------------------------------------
    // validate
    // -----------------------------------------------------------------

    [Fact]
    public async Task Validate_returns_empty_report_for_clean_abox()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "abox-val-empty");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/abox/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("violations").GetArrayLength());
        var counts = body.GetProperty("counts");
        Assert.Equal(0, counts.GetProperty("error").GetInt32());
        Assert.Equal(0, counts.GetProperty("warning").GetInt32());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Validate_flags_placeholder_label_with_delete_fix()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-val-place");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var placeholderIri = await CreateIndividualAsync(client, ksId, "Untitled", dogClass);

        // Override the auto-written rdfs:label to force the placeholder path.
        // (The mint flow writes the label we pass; "Untitled" is in the
        // NonIdentifyingLabels set so the validator will flag it.)
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);

        var response = await client.GetAsync($"/api/knowledge/{ksId}/abox/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var violations = body.GetProperty("violations");
        Assert.Equal(1, violations.GetArrayLength());
        var placeholder = violations[0];
        Assert.Equal("placeholder", placeholder.GetProperty("type").GetString());
        Assert.Equal("error", placeholder.GetProperty("severity").GetString());
        Assert.Equal(placeholderIri, placeholder.GetProperty("individual").GetProperty("iri").GetString());

        var fixes = placeholder.GetProperty("fixes");
        Assert.Equal(1, fixes.GetArrayLength());
        Assert.Equal("delete_individual", fixes[0].GetProperty("op").GetProperty("kind").GetString());
        Assert.Equal(placeholderIri, fixes[0].GetProperty("op").GetProperty("iri").GetString());

        // The ABox graph still has the placeholder quad — validate is
        // read-only.
        Assert.NotEmpty(store.Match(subjectIri: placeholderIri, graphIri: aboxGraph));
    }

    // -----------------------------------------------------------------
    // fix_violation
    // -----------------------------------------------------------------

    [Fact]
    public async Task FixViolation_delete_individual_removes_quads_and_returns_fresh_report()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-fix-delete");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var placeholderIri = await CreateIndividualAsync(client, ksId, "Untitled", dogClass);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(subjectIri: placeholderIri, graphIri: aboxGraph));

        var response = await PostFixAsync(client, ksId, new
        {
            op = new Dictionary<string, object?>
            {
                ["kind"] = "delete_individual",
                ["iri"] = placeholderIri,
            },
            summary = "Drop the placeholder individual",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("violations").GetArrayLength());

        Assert.Empty(store.Match(subjectIri: placeholderIri, graphIri: aboxGraph));

        // Audit row exists with the fix op captured in Detail.
        var audits = LookupAuditEventsFor(app, ksGuid)
            .Where(e => e.Action == "abox.fix_violation").ToList();
        Assert.Single(audits);
    }

    [Fact]
    public async Task FixViolation_relax_range_records_decision_and_updates_tbox()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-fix-relax");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ageProp = await AddTBoxDataPropertyAsync(client, ksId, "age", rangeXsd: "integer");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);

        // Write a non-integer value to age so the validator flags a
        // datatype violation.
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        var ageNode = new OntoNamedNode(ageProp);
        store.AddQuads(new OntoNamedNode(aboxGraph), new[]
        {
            new OntoQuad(
                new OntoNamedNode(rexIri),
                ageNode,
                new OntoLiteral("not-a-number"),
                new OntoNamedNode(aboxGraph)),
        });

        var response = await PostFixAsync(client, ksId, new
        {
            op = new Dictionary<string, object?>
            {
                ["kind"] = "relax_range",
                ["prop"] = ageProp,
                ["prop_label"] = "age",
                ["xsd"] = "integer",
            },
            summary = "Relax age range to text",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The validation_decision table has the remembered preference.
        var decisions = LookupValidationDecisions(app, ksGuid);
        Assert.Single(decisions);
        Assert.Equal("relax", decisions[0].Action);
        Assert.Equal(ageProp, decisions[0].PropertyIri);
        Assert.Equal("age", decisions[0].PropertyLabel);
        Assert.Equal("integer", decisions[0].XsdType);
        Assert.Equal(AuthTestWebApplicationFactory.AdminDisplayName, decisions[0].ResolvedBy);

        // The TBox graph carries the relaxed range (xsd:string) for age.
        var tboxGraph = LookupKsTboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(
            subjectIri: ageProp,
            predicateIri: "http://www.w3.org/2000/01/rdf-schema#range",
            objectIri: "http://www.w3.org/2001/XMLSchema#string",
            graphIri: tboxGraph));
    }

    [Fact]
    public async Task FixViolation_with_unknown_kind_returns_500()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-fix-bad");

        var response = await PostFixAsync(client, ksId, new
        {
            op = new Dictionary<string, object?>
            {
                ["kind"] = "explode_individual",
                ["iri"] = "http://example.com/no-such",
            },
            summary = "",
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // No decisions row written on a rejected op.
        Assert.Empty(LookupValidationDecisions(app, ksGuid));
    }

    // -----------------------------------------------------------------
    // reset
    // -----------------------------------------------------------------

    [Fact]
    public async Task Reset_wipes_abox_graph_and_drops_provenance_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-reset");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ownerClass = await AddTBoxClassAsync(client, ksId, "Owner");
        var ownsProp = await AddTBoxObjectPropertyAsync(client, ksId, "owns");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);
        var aliceIri = await CreateIndividualAsync(client, ksId, "Alice", ownerClass);
        await PostAssertionAsync(client, ksId, new
        {
            subject = rexIri,
            prop = ownsProp,
            kind = "object",
            target = aliceIri,
        });

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(graphIri: aboxGraph));
        var provenanceBefore = LookupProvenanceRows(app, ksGuid);
        Assert.NotEmpty(provenanceBefore);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/reset",
            new { confirm = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("removed_triples").GetInt32() > 0);
        Assert.Equal(provenanceBefore.Count, body.GetProperty("provenance_rows").GetInt32());

        Assert.Empty(store.Match(graphIri: aboxGraph));
        Assert.Empty(LookupProvenanceRows(app, ksGuid));

        var audits = LookupAuditEventsFor(app, ksGuid)
            .Where(e => e.Action == "abox.reset").ToList();
        Assert.Single(audits);
        Assert.NotNull(audits[0].Removed);
    }

    [Fact]
    public async Task Reset_without_confirm_returns_500()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-reset-noconf");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        var before = store.Match(graphIri: aboxGraph).Count;

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/reset",
            new { confirm = false });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Nothing changed.
        Assert.Equal(before, store.Match(graphIri: aboxGraph).Count);
    }

    // -----------------------------------------------------------------
    // validation decisions — list / revoke
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListValidationDecisions_returns_decision_row()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-dec-list");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ageProp = await AddTBoxDataPropertyAsync(client, ksId, "age", rangeXsd: "integer");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        store.AddQuads(new OntoNamedNode(aboxGraph), new[]
        {
            new OntoQuad(
                new OntoNamedNode(rexIri),
                new OntoNamedNode(ageProp),
                new OntoLiteral("oops"),
                new OntoNamedNode(aboxGraph)),
        });
        await PostFixAsync(client, ksId, new
        {
            op = new Dictionary<string, object?>
            {
                ["kind"] = "relax_range",
                ["prop"] = ageProp,
                ["prop_label"] = "age",
                ["xsd"] = "integer",
            },
            summary = "Relax age",
        });

        var response = await client.GetAsync(
            $"/api/knowledge/{ksId}/validation/decisions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("relax", items[0].GetProperty("action").GetString());
        Assert.Equal("age", items[0].GetProperty("property_label").GetString());
    }

    [Fact]
    public async Task RevokeValidationDecision_deletes_row()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "abox-dec-revoke");

        var dogClass = await AddTBoxClassAsync(client, ksId, "Dog");
        var ageProp = await AddTBoxDataPropertyAsync(client, ksId, "age", rangeXsd: "integer");
        var rexIri = await CreateIndividualAsync(client, ksId, "Rex", dogClass);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var aboxGraph = LookupKsAbboxIri(app, ksGuid);
        store.AddQuads(new OntoNamedNode(aboxGraph), new[]
        {
            new OntoQuad(
                new OntoNamedNode(rexIri),
                new OntoNamedNode(ageProp),
                new OntoLiteral("oops"),
                new OntoNamedNode(aboxGraph)),
        });
        await PostFixAsync(client, ksId, new
        {
            op = new Dictionary<string, object?>
            {
                ["kind"] = "relax_range",
                ["prop"] = ageProp,
                ["prop_label"] = "age",
                ["xsd"] = "integer",
            },
            summary = "Relax age",
        });

        var decisions = LookupValidationDecisions(app, ksGuid);
        Assert.Single(decisions);
        var did = decisions[0].Id;

        var response = await client.DeleteAsync(
            $"/api/knowledge/{ksId}/validation/decisions/{did}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(did.ToString(), body.GetProperty("revoked").GetString());

        Assert.Empty(LookupValidationDecisions(app, ksGuid));
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
            var passwordService = new OnToPilot.Authentication.PasswordService();
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

    private static async Task<(long LegacyId, Guid Guid)> CreateKsAsync(
        AuthTestWebApplicationFactory app, HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var legacy = body.GetProperty("id").GetInt64();
        var guid = LookupKsGuid(app, legacy);
        return (legacy, guid);
    }

    private static Guid LookupKsGuid(AuthTestWebApplicationFactory app, long legacyId)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.LegacyId == legacyId)
            .Select(k => k.Id)
            .Single();
    }

    private static string LookupKsAbboxIri(AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => k.GraphIri)
            .Single()
            .TrimEnd('/') + "/abox";
    }

    private static string LookupKsTboxIri(AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => k.GraphIri)
            .Single()
            .TrimEnd('/');
    }

    private static IReadOnlyList<AuditEventEntity> LookupAuditEventsFor(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.AuditEvents.AsNoTracking()
            .Where(e => e.KnowledgeSystemId == ksGuid)
            .ToList();
    }

    private static IReadOnlyList<AboxProvenanceEntity> LookupProvenanceRows(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.AboxProvenances.AsNoTracking()
            .Where(p => p.KnowledgeSystemId == ksGuid)
            .ToList();
    }

    private static IReadOnlyList<ValidationDecisionEntity> LookupValidationDecisions(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.ValidationDecisions.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ksGuid)
            .ToList();
    }

    private static async Task<string> AddTBoxClassAsync(
        HttpClient client, long ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> AddTBoxObjectPropertyAsync(
        HttpClient client, long ksId, string label)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_property", kind = "object", label });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> AddTBoxDataPropertyAsync(
        HttpClient client, long ksId, string label, string rangeXsd)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_property", kind = "data", label, range = rangeXsd });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> CreateIndividualAsync(
        HttpClient client, long ksId, string label, string classIri)
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
        HttpClient client, long ksId, object body)
    {
        return await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/assertions", body);
    }

    private static async Task<HttpResponseMessage> PostFixAsync(
        HttpClient client, long ksId, object body)
    {
        return await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/validate/fix", body);
    }
}