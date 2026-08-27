using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/ontology/edit</c>,
/// <c>/api/knowledge/{ks_id}/ontology/reset</c>, the read-only
/// <c>/api/knowledge/{ks_id}/ontology</c> view (Stage 2), and the supporting
/// <c>documents.impact</c> endpoint. Mirrors the established
/// <see cref="Knowledge.KnowledgeApiTests"/> pattern: real Kestrel,
/// SQLite + per-test temp roots, Oxigraph wired via the per-test
/// <see cref="AuthTestWebApplicationFactory"/> config so the
/// <c>OntologyService</c> singleton has a writable graph handle.
/// </summary>
public sealed class OntologyApiTests
{
    private const string CookieHeader = "isestudio_session";

    [Fact]
    public async Task Rdf_import_accepts_multipart_form_data()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "rdf-import-multipart");
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("<urn:Pump> a <http://www.w3.org/2002/07/owl#Class> ."), "file", "pump.ttl" },
            { new StringContent("auto"), "target" },
            { new StringContent("merge"), "strategy" },
            { new StringContent("turtle"), "format" },
            { new StringContent("urn:base:"), "base_iri" },
        };

        var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rdf_import_returns_python_compatible_response_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "rdf-import-shape");
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class ."), "file", "pump.ttl" },
            { new StringContent("auto"), "target" },
            { new StringContent("merge"), "strategy" },
            { new StringContent("turtle"), "format" },
            { new StringContent("urn:base:"), "base_iri" },
        };

        var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pump.ttl", body.GetProperty("filename").GetString());
        Assert.Equal("turtle", body.GetProperty("format").GetString());
        Assert.Equal("auto", body.GetProperty("target").GetString());
        Assert.Equal("merge", body.GetProperty("strategy").GetString());
        Assert.Equal("urn:base:", body.GetProperty("base_iri").GetString());
        Assert.Equal(1, body.GetProperty("parsed_triples").GetInt32());
        Assert.True(body.TryGetProperty("tbox_added", out _));
        Assert.True(body.TryGetProperty("abox_added", out _));
        Assert.True(body.TryGetProperty("view", out _));
        Assert.True(body.TryGetProperty("open_conflicts", out _));
        Assert.True(body.TryGetProperty("validation", out _));
        Assert.True(body.TryGetProperty("terminology", out _));
        var conflicts = body.GetProperty("open_conflicts");
        Assert.Equal(JsonValueKind.Array, conflicts.ValueKind);

        var store = app.Services.GetRequiredService<ISEStudio.Ontology.StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);
        Assert.Single(store.Match(
            subjectIri: "urn:Pump",
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            objectIri: "http://www.w3.org/2002/07/owl#Class",
            graphIri: graphIri));
        Assert.Equal(1, body.GetProperty("tbox_triples").GetInt32());
        Assert.Equal(0, body.GetProperty("abox_triples").GetInt32());
        Assert.Equal(1, body.GetProperty("tbox_added").GetInt32());
    }

    [Theory]
    [InlineData("target", "bogus-target")]
    [InlineData("strategy", "bogus-strategy")]
    [InlineData("format", "bogus-format")]
    public async Task Rdf_import_rejects_invalid_form_fields(string field, string value)
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, $"rdf-import-invalid-{field}");
        // Send exactly one value per field: the field under test gets the
        // bogus value, the rest get valid defaults. (Sending a duplicate
        // form part lets ASP.NET Core's [FromForm] string binder take the
        // first occurrence and silently drop the override, so the test
        // would see 200 instead of 400.)
        var target = field == "target" ? value : "auto";
        var strategy = field == "strategy" ? value : "merge";
        var format = field == "format" ? value : "turtle";
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class ."), "file", "pump.ttl" },
            { new StringContent(target), "target" },
            { new StringContent(strategy), "strategy" },
            { new StringContent(format), "format" },
        };

        var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(detail.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Rdf_import_replace_strategy_clears_previous_ontology_graph()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "rdf-import-replace");
        var first = new MultipartFormDataContent
        {
            { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class .\n<urn:Valve> a owl:Class ."), "file", "seed.ttl" },
            { new StringContent("tbox"), "target" },
            { new StringContent("merge"), "strategy" },
            { new StringContent("turtle"), "format" },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", first)).StatusCode);

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);
        Assert.NotEmpty(store.Match(graphIri: graphIri));

        var second = new MultipartFormDataContent
        {
            { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:OnlyOne> a owl:Class ."), "file", "replace.ttl" },
            { new StringContent("tbox"), "target" },
            { new StringContent("replace"), "strategy" },
            { new StringContent("turtle"), "format" },
        };
        var replace = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", second);

        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        var body = await replace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("tbox_added").GetInt32());
        Assert.True(body.GetProperty("tbox_removed").GetInt32() >= 1);
        Assert.Empty(store.Match(subjectIri: "urn:Pump", graphIri: graphIri));
        Assert.Empty(store.Match(subjectIri: "urn:Valve", graphIri: graphIri));
        Assert.NotEmpty(store.Match(subjectIri: "urn:OnlyOne", graphIri: graphIri));
    }

    [Fact]
    public async Task Rdf_import_active_extraction_returns_409()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "rdf-import-active");
        await SeedActiveExtractionJobAsync(app, ksId);
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class ."), "file", "pump.ttl" },
            { new StringContent("auto"), "target" },
            { new StringContent("merge"), "strategy" },
            { new StringContent("turtle"), "format" },
        };

        var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Rdf_import_writes_grouped_audit_rows_for_changed_graphs()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "rdf-import-audit");
        var multipart = new MultipartFormDataContent
        {
            { new StringContent("""
                @prefix owl: <http://www.w3.org/2002/07/owl#> .
                @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
                <urn:Pump> a owl:Class .
                <urn:p101> rdf:type <urn:Pump> .
                """), "file", "mixed.ttl" },
            { new StringContent("auto"), "target" },
            { new StringContent("merge"), "strategy" },
            { new StringContent("turtle"), "format" },
        };

        var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audits = LookupAuditEventsFor(app, ksId).Where(a => a.Action == "rdf.import").ToList();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.False(string.IsNullOrWhiteSpace(a.GroupId)));
        Assert.Single(audits.Select(a => a.GroupId).Distinct());
        Assert.Contains(audits, a => a.Summary.Contains("ontology", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audits, a => a.Summary.Contains("instances", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------
    // Edit
    // -----------------------------------------------------------------

    [Fact]
    public async Task Edit_add_class_creates_class_and_writes_audit_with_ntriples_diff()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-edit");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal", comment = "An animal." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var iri = body.GetProperty("iri").GetString();
        Assert.False(string.IsNullOrEmpty(iri));
        Assert.EndsWith("Animal", iri);

        // The graph now contains the class declaration + label + comment.
        Assert.Single(store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            objectIri: "http://www.w3.org/2002/07/owl#Class",
            graphIri: graphIri));
        Assert.Single(store.Match(
            subjectIri: iri,
            predicateIri: "http://www.w3.org/2000/01/rdf-schema#label",
            graphIri: graphIri));

        // Audit row captured the byte-exact N-Quads diff (the three
        // triples the editor added in this op).
        var audits = LookupAuditEventsFor(app, ksId);
        var editAudit = audits.Single(e => e.Action == "ontology.edit");
        Assert.NotNull(editAudit.Added);
        Assert.True(editAudit.Added!.Length > 0);
        Assert.Contains("Animal", System.Text.Encoding.UTF8.GetString(editAudit.Added));
        Assert.Equal(graphIri, editAudit.Graph);
    }

    [Fact]
    public async Task Edit_add_class_duplicate_label_is_idempotent()
    {
        // The editor's "ensure labelled class" helper gates on the
        // rdf:type triple being present, so a second add_class with the
        // same label is a no-op (no new triples, no audit row gains
        // anything on the Added side beyond the existing comment).
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-idempotent");

        var first = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstIri = firstBody.GetProperty("iri").GetString();

        var second = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstIri, secondBody.GetProperty("iri").GetString());

        // Two audit rows; the second carries no Added content — the
        // editor's ensure-helper saw the existing class and wrote
        // nothing new, so Added is null (we don't materialise an empty
        // byte[] just to be able to say "zero bytes").
        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "ontology.edit").ToList();
        Assert.Equal(2, audits.Count);
        var secondAudit = audits[1];
        Assert.True(secondAudit.Added is null || secondAudit.Added.Length == 0);
    }

    [Fact]
    public async Task Ontology_export_returns_serialized_tbox_as_raw_text()
    {
        // The ontology.export arm was a Stage-1 placeholder returning "" —
        // the frontend's Export menu downloaded an empty file. Wire it to
        // RdfExportService and return raw text/turtle (not a JSON-quoted
        // string) so the downloaded .ttl is valid RDF.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-export");
        var add = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Pump" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        var response = await client.GetAsync($"/api/knowledge/{ksId}/ontology/export?fmt=turtle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/turtle", response.Content.Headers.ContentType?.MediaType);
        var rdf = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(rdf);
        Assert.Contains("Pump", rdf);
        // Raw text, not a JSON-quoted string.
        Assert.False(rdf.StartsWith('"'));
    }

    [Theory]
    [InlineData("jsonld", "application/ld+json")]
    [InlineData("rdfxml", "application/rdf+xml")]
    public async Task Ontology_export_serializes_non_turtle_formats(string fmt, string mediaType)
    {
        // RDF/XML and JSON-LD round-trip through dotNetRDF's writers
        // (RdfXmlWriter / JsonLdWriter) after projecting the Oxigraph quads
        // onto a dotNetRDF graph. The frontend's Export menu offers these
        // alongside turtle / ntriples, so they must return non-empty raw
        // content with the matching media type.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, $"ontology-export-{fmt}");
        var add = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Valve" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        var response = await client.GetAsync($"/api/knowledge/{ksId}/ontology/export?fmt={fmt}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        var rdf = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(rdf);
        Assert.Contains("Valve", rdf);
    }

    [Fact]
    public async Task Ontology_export_rejects_unsupported_format_with_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-export-badfmt");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/ontology/export?fmt=yaml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Edit_add_subclass_writes_two_classes_and_one_axiom()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-subclass");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);

        // Seed Animal first so the subclass axiom has a super-class to
        // point at without relying on the ensure-helper side effect.
        var addAnimal = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        Assert.Equal(HttpStatusCode.OK, addAnimal.StatusCode);
        var animalIri = (await addAnimal.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("iri").GetString()!;

        var addDog = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Dog" });
        Assert.Equal(HttpStatusCode.OK, addDog.StatusCode);
        var dogIri = (await addDog.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("iri").GetString()!;

        var addAxiom = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_axiom", type = "subclass", sub = dogIri, @super = animalIri });
        Assert.Equal(HttpStatusCode.OK, addAxiom.StatusCode);

        // The subclass axiom landed in the graph (subject + graph
        // scoped; the predicate is the canonical rdfs:subClassOf).
        Assert.Single(store.Match(
            subjectIri: dogIri,
            predicateIri: "http://www.w3.org/2000/01/rdf-schema#subClassOf",
            objectIri: animalIri,
            graphIri: graphIri));
    }

    [Fact]
    public async Task Edit_delete_class_captures_removed_quads_in_audit()
    {
        // The ABox cascade requires a hand-built individual quad that
        // points at the class; the test asserts that the audit row's
        // Removed blob contains the class triples (the ABox cascade is
        // tested separately via the ontology.feature integration tests).
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-delete");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);

        var add = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var iri = (await add.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("iri").GetString()!;

        var delete = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "delete_class", iri });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        // The class is gone from the graph.
        Assert.Empty(store.Match(
            subjectIri: iri,
            graphIri: graphIri));

        // The most recent audit row carries the Removed blob.
        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "ontology.edit").ToList();
        var deleteAudit = audits.Last();
        Assert.NotNull(deleteAudit.Removed);
        Assert.Contains("Animal", System.Text.Encoding.UTF8.GetString(deleteAudit.Removed!));
    }

    [Fact]
    public async Task Edit_with_invalid_op_returns_500_with_envelope()
    {
        // The editor's op parser raises OntologyEditException which
        // the service wraps as InvalidOperationException. The global
        // FastApiErrorMiddleware doesn't translate that to a 4xx; the
        // contract for this slice is therefore 500 with the
        // {"detail": "Internal server error"} envelope (no leak of
        // the editor exception message). The contract is documented in
        // the IntegrationTest suite so callers know to keep the
        // controller surface thin.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-bad-op");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "definitely_not_a_real_op", label = "Anything" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Internal server error",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Edit_without_editor_role_returns_500_and_store_stays_empty()
    {
        // The service throws InvalidOperationException when the actor
        // doesn't hold the Editor role; the global
        // FastApiErrorMiddleware doesn't translate that to a 4xx, so
        // the contract for this slice is 500 with the standard
        // {"detail": "Internal server error"} envelope. What matters
        // is that the graph is untouched — the role check runs before
        // any edit reaches the editor.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-nonadmin");

        var (aliceClient, _) = await SeedSecondUserAsync(app);

        var response = await aliceClient.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Sneaky" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // The store stayed untouched — the role gate refused the
        // mutation before it reached the editor.
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);
        Assert.Empty(store.Match(graphIri: graphIri));
    }

    // -----------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------

    [Fact]
    public async Task Reset_clears_graphs_and_writes_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-reset");

        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = LookupKsGraphIri(app, ksId);
        var aboxIri = graphIri.TrimEnd('/') + "/abox";

        // Seed a couple of classes so reset has something to clear.
        await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Animal" });
        await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Dog" });
        Assert.True(store.Match(graphIri: graphIri).Count > 0);

        var reset = await client.PostAsync(
            $"/api/knowledge/{ksId}/ontology/reset",
            new StringContent("{}", System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json")));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // Both TBox and ABox graphs are empty.
        Assert.Empty(store.Match(graphIri: graphIri));
        Assert.Empty(store.Match(graphIri: aboxIri));

        // Audit row carries the Removed blob for the cleared triples.
        var audits = LookupAuditEventsFor(app, ksId)
            .Where(e => e.Action == "ontology.reset").ToList();
        var resetAudit = audits.Single();
        Assert.NotNull(resetAudit.Removed);
        Assert.Contains("Animal", System.Text.Encoding.UTF8.GetString(resetAudit.Removed!));
        Assert.Contains("Dog", System.Text.Encoding.UTF8.GetString(resetAudit.Removed!));
    }

    // -----------------------------------------------------------------
    // Impact
    // -----------------------------------------------------------------

    [Fact]
    public async Task Impact_walks_axiom_provenance_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "impact-walk");

        // Upload + parse a tiny document so chunks exist.
        var upload = await UploadAsync(client, ksId, "i.txt", "hello world\n", folder: "/");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var created = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var docId = created.GetProperty("id").GetGuid();
        await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);

        // Seed axiom provenance pointing at one of this doc's chunks.
        var chunkId = LookupFirstChunkId(app, ksId);
        SeedAxiomProvenance(app, ksId, chunkId, "subClassOf|dog|Animal");
        SeedAxiomProvenance(app, ksId, chunkId, "class|Animal");

        var impact = await client.GetAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/impact");
        Assert.Equal(HttpStatusCode.OK, impact.StatusCode);
        var body = await impact.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(docId, body.GetProperty("document_id").GetGuid());
        var systems = body.GetProperty("systems");
        Assert.Equal(1, systems.GetArrayLength());
        var ksEntry = systems[0];
        Assert.Equal(ksId, ksEntry.GetProperty("knowledge_system_id").GetGuid());
        var axioms = ksEntry.GetProperty("axioms");
        Assert.Equal(2, axioms.GetArrayLength());
        var keys = axioms.EnumerateArray()
            .Select(a => a.GetProperty("axiom_key").GetString())
            .ToList();
        Assert.Contains("subClassOf|dog|Animal", keys);
        Assert.Contains("class|Animal", keys);
    }

    // -----------------------------------------------------------------
    // View (Stage 2 — read-only envelope for the frontend OntologyView)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Get_ontology_returns_full_envelope_with_all_top_level_keys()
    {
        // The Stage 2 read endpoint returns the curated
        // OntologyResponse envelope (classes / object_properties /
        // data_properties / axioms / labels / stats / knowledge_system)
        // produced by OntologyService.GetViewAsync and serialised via
        // the global SnakeCaseLower naming policy. Asserting every
        // top-level key is on the wire catches accidental shape
        // regressions that would otherwise only surface as silent
        // frontend blank screens.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-view");

        var res = await client.GetAsync($"/api/knowledge/{ksId}/ontology");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("classes", out _));
        Assert.True(body.TryGetProperty("object_properties", out _));
        Assert.True(body.TryGetProperty("data_properties", out _));
        Assert.True(body.TryGetProperty("axioms", out _));
        Assert.True(body.TryGetProperty("labels", out _));
        Assert.True(body.TryGetProperty("stats", out _));
        Assert.True(body.TryGetProperty("knowledge_system", out _));

        var axioms = body.GetProperty("axioms");
        Assert.True(axioms.TryGetProperty("subclass_of", out _));
        Assert.True(axioms.TryGetProperty("disjoint_with", out _));
        Assert.True(axioms.TryGetProperty("equivalent_class", out _));

        // The freshly-created KS has no classes / properties / axioms,
        // so the stats counters should all be zero.
        var stats = body.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("class_count").GetInt32());
        Assert.Equal(0, stats.GetProperty("property_count").GetInt32());
        Assert.Equal(0, stats.GetProperty("axiom_count").GetInt32());
    }

    [Fact]
    public async Task Get_ontology_returns_404_for_unknown_KS()
    {
        // An unknown KS Guid must surface as HTTP 404 via the global
        // FastApiErrorMiddleware KeyNotFoundException branch (proves
        // the Stage 2 dispatcher's "ontology.get" arm propagates the
        // service's KeyNotFoundException correctly through the
        // facade). Known caveat (R12 — pre-existing
        // InternalOperationDispatcher.cs:389 ContinueWith(.Result)
        // wrapper wraps the exception in AggregateException which
        // the middleware does not currently unwrap; deferred to
        // Task 11). When that wrapper is replaced with `await`, this
        // test should turn green.
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var res = await client.GetAsync($"/api/knowledge/{Guid.NewGuid()}/ontology");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // -----------------------------------------------------------------
    // Provenance: sources + provenance (wire ontology.sources / ontology.provenance)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Ontology_sources_returns_aggregated_documents()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-sources-http");
        var db = app.CreateDbContext();
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, OriginalFilename = "manual.pdf",
            Folder = "/", Sha256 = Guid.NewGuid().ToString("N"), Ext = "pdf",
            SizeBytes = 1, StoragePath = "aa/bb/x", UploadedAt = DateTimeOffset.UtcNow, ParseStatus = "parsed",
        };
        db.Documents.Add(doc); await db.SaveChangesAsync();
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id, Idx = 0, Text = "t", CharStart = 0, CharEnd = 1,
            TokenEstimate = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.AxiomProvenances.Add(new AxiomProvenanceEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, AxiomKey = "subClassOf|Pump|Device",
            ChunkId = chunk.Id, Method = "extraction", ActorName = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync($"/api/knowledge/{ksId}/sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        var row = body.EnumerateArray().First();
        Assert.Equal(doc.Id.ToString(), row.GetProperty("document_id").GetString());
        Assert.Equal("manual.pdf", row.GetProperty("filename").GetString());
        Assert.True(row.GetProperty("exists").GetBoolean());
        Assert.Equal(1, row.GetProperty("chunk_count").GetInt32());
        Assert.Equal(1, row.GetProperty("axiom_count").GetInt32());
    }

    [Fact]
    public async Task Ontology_provenance_returns_grouped_sources()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "ontology-provenance-http");
        var db = app.CreateDbContext();
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, OriginalFilename = "m.pdf",
            Folder = "/", Sha256 = Guid.NewGuid().ToString("N"), Ext = "pdf",
            SizeBytes = 1, StoragePath = "aa/bb/x", UploadedAt = DateTimeOffset.UtcNow, ParseStatus = "parsed",
        };
        db.Documents.Add(doc); await db.SaveChangesAsync();
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id, Idx = 0, Text = "t", CharStart = 0, CharEnd = 1,
            TokenEstimate = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.AxiomProvenances.Add(new AxiomProvenanceEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, AxiomKey = "domain|hasFlow|Pump",
            ChunkId = chunk.Id, Method = "extraction", ActorName = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync($"/api/knowledge/{ksId}/provenance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        var group = body.EnumerateArray().First();
        Assert.Equal("domain|hasFlow|Pump", group.GetProperty("axiom_key").GetString());
        var sources = group.GetProperty("sources");
        Assert.Single(sources.EnumerateArray());
        var s = sources.EnumerateArray().First();
        Assert.Equal(chunk.Id.ToString(), s.GetProperty("chunk_id").GetString());
        Assert.Equal(doc.Id.ToString(), s.GetProperty("document_id").GetString());
        Assert.Equal("extraction", s.GetProperty("method").GetString());
        Assert.Equal("admin", s.GetProperty("actor").GetString());
    }

    // -----------------------------------------------------------------
    // History: get + rollback (wire history.get / history.rollback)
    // -----------------------------------------------------------------

    [Fact]
    public async Task History_get_returns_paginated_events()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "history-get-http");
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, ActorId = admin.Id, ActorName = "Admin",
            Action = "ontology.edit", Summary = "edit A", Graph = null,
            Added = System.Text.Encoding.UTF8.GetBytes("<urn:A> a <urn:C> <urn:g> .\n"),
            Removed = null, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync($"/api/knowledge/{ksId}/history?limit=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Single(body.GetProperty("items").EnumerateArray());
        var item = body.GetProperty("items").EnumerateArray().First();
        Assert.Equal("ontology.edit", item.GetProperty("action").GetString());
        Assert.True(item.GetProperty("can_rollback").GetBoolean());
        Assert.False(item.TryGetProperty("added", out _));  // binary diff omitted
    }

    [Fact]
    public async Task History_rollback_reverts_event_and_returns_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var ksId = await CreateKsAsync(client, "history-rollback-http");
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var graphIri = $"http://example.com/graph/history-rollback-http";
        var gName = new Oxigraph.NamedNode(graphIri);
        store.AddQuads(gName, new[] { new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:X"), new Oxigraph.NamedNode("urn:p"),
            new Oxigraph.NamedNode("urn:Y"), gName) });
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, ActorId = admin.Id, ActorName = "Admin",
            Action = "ontology.edit", Summary = "add X", Graph = null,
            Added = store.DumpNQuads(gName), Removed = null, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var eventId = db.AuditEvents.AsNoTracking().First(e => e.KnowledgeSystemId == ksId).Id;

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/history/{eventId}/rollback", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("undone").GetInt32());
        Assert.True(body.TryGetProperty("view", out _));
        Assert.True(body.TryGetProperty("open_conflicts", out _));
        // 三元已回滚移除
        Assert.Empty(store.Match(subjectIri: "urn:X", predicateIri: "urn:p", objectIri: "urn:Y", graphIri: graphIri));
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

    private static async Task SeedActiveExtractionJobAsync(
        AuthTestWebApplicationFactory app, Guid ksId)
    {
        // Mirror the VocabularyApiTests "extraction active" pattern: a single
        // running extraction row is enough for the dispatcher's
        // RejectIfExtractionActiveAsync to surface the 409 envelope.
        var db = app.CreateDbContext();
        var job = new ExtractionJobEntity
        {
            KnowledgeSystemId = ksId,
            Kind = "tbox",
            Status = JobStatus.Running.ToWire(),
            Model = "gpt-4",
            ChunkIds = new List<int>(),
            TotalChunks = 0,
            ProcessedChunks = 0,
            AxiomsAdded = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
            Phase = string.Empty,
        };
        db.ExtractionJobs.Add(job);
        await db.SaveChangesAsync();
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

    private static string LookupKsGraphIri(AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksId)
            .Select(k => k.GraphIri)
            .Single();
    }

    private static IReadOnlyList<AuditEventEntity> LookupAuditEventsFor(
        AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        return db.AuditEvents.AsNoTracking()
            .Where(e => e.KnowledgeSystemId == ksId)
            .ToList();
    }

    private static Guid LookupFirstChunkId(AuthTestWebApplicationFactory app, Guid ksId)
    {
        var db = app.CreateDbContext();
        // SQLite refuses DateTimeOffset in ORDER BY; materialise + sort
        // client-side (same workaround as KnowledgeService.ListAsync).
        var docId = db.Documents.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ksId)
            .ToList()
            .OrderBy(d => d.UploadedAt)
            .Select(d => d.Id)
            .First();
        return db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == docId)
            .OrderBy(c => c.Idx)
            .Select(c => c.Id)
            .First();
    }

    private static void SeedAxiomProvenance(
        AuthTestWebApplicationFactory app, Guid ksId, Guid chunkId, string axiomKey)
    {
        var db = app.CreateDbContext();
        db.AxiomProvenances.Add(new AxiomProvenanceEntity
        {
            KnowledgeSystemId = ksId,
            AxiomKey = axiomKey,
            ChunkId = chunkId,
            Method = "extraction",
            ActorName = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid ksId, string fileName, string content, string folder)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var multipart = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", fileName },
            { new StringContent(folder), "folder" },
        };
        return await client.PostAsync($"/api/knowledge/{ksId}/documents/upload", multipart);
    }
}
