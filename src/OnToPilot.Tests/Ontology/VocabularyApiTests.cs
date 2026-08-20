using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Authentication;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// HTTP-level contract tests for the B8 vocabulary surface. The 16 internal
/// <c>vocabulary.*</c> arms now route through <see cref="VocabularyService"/>
/// / <see cref="VocabularyProposalService"/> / <see cref="TerminologyAgent"/>
/// via the dispatcher's <c>InvokeVocabularyXxxAsync</c> helpers, so the SKOS
/// read/write, proposal, and suggest paths are real.
///
/// <para>Two of the ten tests are external/published smoke tests (Bearer-token
/// authenticated): those surfaces enforce the Viewer role gate against the
/// token principal's user id, which resolves to an empty placeholder in the
/// test factory — the smoke assertions confirm the wire path returns 200 with
/// a parseable envelope, not that data is returned.</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class VocabularyApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // -----------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Get_vocabulary_returns_skos_view_with_schemes_and_concepts()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await SeedKnowledgeSystemAsync(app, client, "b8-get");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/vocabulary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array,
            json.GetProperty("schemes").ValueKind);
        Assert.Equal(JsonValueKind.Array,
            json.GetProperty("concepts").ValueKind);
        Assert.Equal(JsonValueKind.Object,
            json.GetProperty("stats").ValueKind);
        Assert.Equal(0,
            json.GetProperty("stats").GetProperty("scheme_count").GetInt32());

        // snake_case wire shape (Task 1) — raw body must NOT contain PascalCase
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"schemes\"", raw);
        Assert.Contains("\"concepts\"", raw);
        Assert.DoesNotContain("\"Schemes\"", raw);
        Assert.DoesNotContain("\"Concepts\"", raw);
    }

    [Fact]
    public async Task List_concepts_with_filters_returns_paginated_page()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await SeedKnowledgeSystemAsync(app, client, "b8-list");

        var response = await client.GetAsync(
            $"/api/knowledge/{ksId}/vocabulary/concepts?limit=10&offset=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("total").ValueKind);
    }

    [Fact]
    public async Task Create_concept_writes_to_vocabulary_graph_and_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-create");

        var schemeIri = await CreateSchemeAsync(app, client, ksId, "Animals");

        var create = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/concepts",
            new
            {
                iri = (string?)null,
                scheme_iri = schemeIri,
                pref_label = "Animal",
                language = "en",
                alt_labels = Array.Empty<object>(),
                hidden_labels = Array.Empty<object>(),
                broader = Array.Empty<string>(),
                related = Array.Empty<string>(),
                description = "A living organism.",
                notation = "",
                status = "active",
                origin = "manual",
                mapped_entity_iri = (string?)null,
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var conceptBody = await create.Content.ReadFromJsonAsync<JsonElement>();
        var conceptIri = conceptBody.GetProperty("iri").GetString();
        Assert.False(string.IsNullOrEmpty(conceptIri));

        // Vocabulary graph should now contain an rdf:type owl:Concept triple.
        var store = app.Services.GetRequiredService<OnToPilot.Ontology.StoreWrapper>();
        var vocabGraph = LookupKsVocabIri(app, ksGuid);
        Assert.NotEmpty(store.Match(
            subjectIri: conceptIri,
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            graphIri: vocabGraph));

        // Audit row should capture the add.
        var db = app.CreateDbContext();
        Assert.NotNull(db.AuditEvents.SingleOrDefault(
            e => e.KnowledgeSystemId == ksGuid
                 && e.Action == "vocabulary.create_concept"
                 && e.Added != null));
    }

    [Fact]
    public async Task Update_concept_replaces_labels_and_writes_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-update");

        var schemeIri = await CreateSchemeAsync(app, client, ksId, "Animals");
        var conceptIri = await CreateConceptAsync(
            app, client, ksId, schemeIri, "Cat");

        var update = await client.PatchAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/concepts",
            new
            {
                iri = conceptIri,
                scheme_iri = schemeIri,
                pref_label = "Feline",
                language = "en",
                alt_labels = Array.Empty<object>(),
                hidden_labels = Array.Empty<object>(),
                broader = Array.Empty<string>(),
                related = Array.Empty<string>(),
                description = "Updated.",
                notation = "",
                status = "active",
                origin = "manual",
                mapped_entity_iri = (string?)null,
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Feline", updated.GetProperty("display_label").GetString());

        var db = app.CreateDbContext();
        Assert.NotNull(db.AuditEvents.SingleOrDefault(
            e => e.KnowledgeSystemId == ksGuid
                 && e.Action == "vocabulary.update_concept"));
    }

    [Fact]
    public async Task Delete_concept_removes_concept_from_graph_and_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-delete");

        var schemeIri = await CreateSchemeAsync(app, client, ksId, "Animals");
        var conceptIri = await CreateConceptAsync(
            app, client, ksId, schemeIri, "Dog");

        // Note: the DELETE route has no {concept_id} segment, so the IRI must
        // travel in the body. The dispatcher's delete helper now reads the
        // body before falling back to ResourceId (null for this route), so
        // the wire path reaches VocabularyService.DeleteConceptAsync and
        // both removes the concept from the graph AND writes an audit row.
        var delete = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/knowledge/{ksId}/vocabulary/concepts")
        {
            Content = JsonContent.Create(new { iri = conceptIri }),
        });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var raw = await delete.Content.ReadAsStringAsync();
        Assert.Contains("removed_triples", raw);

        // Graph should no longer carry any triple whose subject is the
        // concept IRI in the vocabulary graph. A no-op delete would still
        // pass the audit assertion above, so this is the load-bearing
        // check that the SKOS write actually happened.
        var store = app.Services.GetRequiredService<OnToPilot.Ontology.StoreWrapper>();
        var vocabGraph = LookupKsVocabIri(app, ksGuid);
        var remaining = store.Match(
            subjectIri: conceptIri,
            graphIri: vocabGraph);
        Assert.Empty(remaining);

        var db = app.CreateDbContext();
        Assert.NotNull(db.AuditEvents.SingleOrDefault(
            e => e.KnowledgeSystemId == ksGuid
                 && e.Action == "vocabulary.delete_concept"));
    }

    [Fact]
    public async Task Create_scheme_with_extraction_active_returns_409()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-409");

        // Seed a running extraction job so the role-gated scheme write hits the
        // 409 path inside VocabularyService.RejectExtractionAsync.
        var db = app.CreateDbContext();
        var existingJob = new ExtractionJobEntity
        {
            LegacyId = TestLegacyIds.Next("extraction_job"),
            KnowledgeSystemId = ksGuid,
            Kind = "tbox",
            Status = "running",
            Model = "gpt-4",
            ChunkIds = new List<int>(),
            TotalChunks = 0,
            ProcessedChunks = 0,
            AxiomsAdded = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
            Phase = string.Empty,
        };
        db.ExtractionJobs.Add(existingJob);
        db.SaveChanges();
        db.Entry(existingJob).State = EntityState.Detached;

        var create = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/schemes",
            new
            {
                iri = (string?)null,
                title = "Blocked Scheme",
                default_language = "en",
                description = "test",
                origin = "manual",
            });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
    }

    [Fact]
    public async Task Sync_runs_TerminologyService_and_audits_added_concepts()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-sync");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/sync", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // TerminologyResult envelope: terms_added / terms_mapped / proposals_queued
        Assert.Equal(JsonValueKind.Number,
            json.GetProperty("terms_added").ValueKind);

        var db = app.CreateDbContext();
        Assert.NotNull(db.AuditEvents.SingleOrDefault(
            e => e.KnowledgeSystemId == ksGuid
                 && e.Action == "vocabulary.sync"));
    }

    [Fact]
    public async Task Sync_with_terminology_failure_rolls_back_vocabulary_graph()
    {
        // Verify the CaptureAsync wrap added in the B7c hardening slice:
        // when the inner TerminologyService.SyncCore throws partway through
        // (after writing some quads), the vocabulary graph must roll back
        // to pre-state instead of leaving partial commits. The production
        // TerminologyService swallows inner exceptions and surfaces them as
        // TerminologyResult.Error, which makes the rollback path
        // unreachable through the real implementation; we swap in a stub
        // that writes a quad and then throws to exercise
        // VocabularyService's catch + MarkError + rethrow wiring.
        await using var app = new RollbackTerminologyFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-sync-rb");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/sync", new { });
        // The stub throws InvalidOperationException after writing its
        // partial mutation; VocabularyService.SyncAsync rethrows, so the
        // HTTP layer surfaces 500.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Post-state assertion: the vocabulary graph must NOT carry the
        // stub's partial mutation quad because the CaptureAsync rolled
        // the graph back to its pre-state snapshot on dispose.
        var store = app.Services.GetRequiredService<StoreWrapper>();
        var ksc = LookupKsContext(app, ksGuid);
        var postQuads = store.Match(graph: new Oxigraph.NamedNode(ksc.VocabularyGraph));
        Assert.DoesNotContain(postQuads,
            q => q.Subject is Oxigraph.NamedNode n
                 && n.Value.EndsWith("/partial-mutation-marker", StringComparison.Ordinal));

        // Audit row is NOT written because the exception escapes the
        // capture block before the post-sync audit code runs. That is the
        // load-bearing difference vs the happy path: partial-failure
        // callers see no audit row.
        var db = app.CreateDbContext();
        Assert.Null(db.AuditEvents.SingleOrDefault(
            e => e.KnowledgeSystemId == ksGuid
                 && e.Action == "vocabulary.sync"));
    }

    [Fact]
    public async Task Suggest_with_fake_chat_creates_pending_proposals()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        // Seed the chunk + provider + document FIRST so we know the real
        // chunk LegacyId; then prime the FakeChat reply to cite it. Without
        // this, TryBuildProposal drops every proposal whose source ids are
        // not in the loaded set and we get total=0.
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-suggest");
        var chunkLegacyId = SeedSuggestionFixtureAsync(app, ksGuid);

        FakeChatClientFactory.Default.UseClient(
            new FakeChat().EnqueueTerminologyProposal(3, sourceChunkIds: new long[] { chunkLegacyId }));

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/suggest",
            new
            {
                scheme_iri = "http://example.org/scheme",
                chunk_ids = new long[] { chunkLegacyId },
                model = (string?)null,
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("total").GetInt32());

        var db = app.CreateDbContext();
        Assert.Equal(3, db.TermProposals.Count(
            p => p.KnowledgeSystemId == ksGuid && p.Status == "pending"));
    }

    [Fact]
    public async Task External_vocabulary_concepts_smoke_with_token_returns_200()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-ext");
        var publicId = LookupKsPublicId(app, ksGuid);
        var token = MintVocabularyToken(app, ksGuid);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/knowledge-systems/{publicId}/vocabulary/concepts");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task Published_vocabulary_export_smoke_with_token_returns_200()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-pub");
        // PublishedController returns 503 + Retry-After when the KS has no
        // ReleaseDeployment (no deployment == "provisioning"). Seed an
        // active deployment + matching release so the smoke test exercises
        // the 200 path; the brief's "401 / 403 / 503 + Retry-After / 410"
        // contract tests for the other branches live elsewhere.
        SeedActiveReleaseAsync(app, ksGuid);
        var publicId = LookupKsPublicId(app, ksGuid);
        var token = MintVocabularyToken(app, ksGuid);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/knowledge-systems/{publicId}/published/vocabulary/export");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Export returns a turtle / n-quads string (possibly empty for a
        // KS with no vocabulary data in the test factory).
        var raw = await response.Content.ReadAsStringAsync();
        Assert.NotNull(raw);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<string> CreateSchemeAsync(
        AuthTestWebApplicationFactory app, HttpClient client, Guid ksId, string title)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/schemes",
            new
            {
                iri = (string?)null,
                title,
                default_language = "en",
                description = "test scheme",
                origin = "manual",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<string> CreateConceptAsync(
        AuthTestWebApplicationFactory app, HttpClient client, Guid ksId,
        string schemeIri, string prefLabel)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/concepts",
            new
            {
                iri = (string?)null,
                scheme_iri = schemeIri,
                pref_label = prefLabel,
                language = "en",
                alt_labels = Array.Empty<object>(),
                hidden_labels = Array.Empty<object>(),
                broader = Array.Empty<string>(),
                related = Array.Empty<string>(),
                description = "test concept",
                notation = "",
                status = "active",
                origin = "manual",
                mapped_entity_iri = (string?)null,
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            var passwordService = new PasswordService();
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

    private static async Task<(Guid KsId, Guid KsGuid)> SeedKnowledgeSystemAsync(
        AuthTestWebApplicationFactory app, HttpClient client, string tag)
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
        var ksId = body.GetProperty("id").GetGuid();
        return (ksId, ksId);
    }

    private static string LookupKsPublicId(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => k.PublicId)
            .Single();
    }

    private static string LookupKsVocabIri(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => k.GraphIri)
            .Single()
            .TrimEnd('/') + "/vocabulary";
    }

    private static string MintVocabularyToken(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var service = app.Services
            .GetRequiredService<OnToPilot.Authentication.IKnowledgeApiTokenService>();
        var minted = service.CreateAsync(
            new KnowledgeApiTokenCreateRequest(
                KnowledgeSystemId: ksGuid,
                CreatedById: null,
                Name: "vocabulary-smoke",
                Scopes: new[] { "vocabulary:read" },
                ExpiresAt: null),
            CancellationToken.None).GetAwaiter().GetResult();
        return minted.Plaintext;
    }

    /// <summary>
    /// Seed a Provider + Document + Chunk so
    /// <see cref="OnToPilot.Extraction.TerminologyAgent.SuggestAsync"/>
    /// passes the empty-chunkIds / empty-loadedChunks short-circuit gates
    /// and reaches the LLM call. Returns the seeded chunk's LegacyId so
    /// the caller can pass it through <c>chunk_ids</c>.
    /// </summary>
    private static long SeedSuggestionFixtureAsync(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        var provider = new ProviderEntity
        {
            LegacyId = TestLegacyIds.Next("providers"),
            Name = "fake-suggest",
            BaseUrl = "https://example.invalid",
            ApiKey = "test",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.SaveChanges();

        db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .ExecuteUpdate(s => s.SetProperty(k => k.LlmProviderId, (Guid?)provider.Id));

        var document = new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("documents"),
            KnowledgeSystemId = ksGuid,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "suggest.txt",
            Folder = "/",
            Ext = ".txt",
            Mime = "text/plain",
            StoragePath = "test/suggest.txt",
            UploadedAt = DateTimeOffset.UtcNow,
            ParseStatus = "ready",
            ChunkCount = 1,
            TextCharCount = 16,
        };
        db.Documents.Add(document);
        db.SaveChanges();

        var chunk = new ChunkEntity
        {
            LegacyId = TestLegacyIds.Next("chunks"),
            DocumentId = document.Id,
            Idx = 0,
            Text = "Animals are living creatures.",
            CharStart = 0,
            CharEnd = 16,
            TokenEstimate = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        db.Entry(chunk).State = EntityState.Detached;
        db.Entry(document).State = EntityState.Detached;
        db.Entry(provider).State = EntityState.Detached;
        return chunk.LegacyId;
    }

    /// <summary>
    /// Seed an active <c>ReleaseDeployment</c> + matching
    /// <c>OntologyRelease</c> so <see cref="PublishedController"/>'s
    /// release-resolution path lands on the <c>Active</c> branch instead of
    /// returning 503 + Retry-After (no deployment == "provisioning").
    /// </summary>
    private static void SeedActiveReleaseAsync(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        var release = new OntologyReleaseEntity
        {
            LegacyId = TestLegacyIds.Next("ontology_releases"),
            KnowledgeSystemId = ksGuid,
            Version = "v1",
            Status = "published",
            Title = "smoke",
            Notes = string.Empty,
            SnapshotDir = string.Empty,
            Manifest = null,
            CreatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.OntologyReleases.Add(release);
        db.SaveChanges();
        var deployment = new ReleaseDeploymentEntity
        {
            LegacyId = TestLegacyIds.Next("release_deployments"),
            KnowledgeSystemId = ksGuid,
            ReleaseId = release.Id,
            Status = "active",
            TboxGraphIri = string.Empty,
            VocabularyGraphIri = string.Empty,
            AboxGraphIri = string.Empty,
            StatementCount = 0,
            ProvenanceCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow,
        };
        db.ReleaseDeployments.Add(deployment);
        db.SaveChanges();
        db.Entry(release).State = EntityState.Detached;
        db.Entry(deployment).State = EntityState.Detached;
    }

    private static KsContext LookupKsContext(
        AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        var ks = db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => new { k.GraphIri, k.BaseIri })
            .Single();
        return new KsContext(ks.GraphIri, ks.BaseIri);
    }

    /// <summary>
    /// Test fixture that swaps the production
    /// <see cref="TerminologyService"/> for a stub that writes one quad
    /// to the vocabulary graph and then throws. Used by
    /// <c>Sync_with_terminology_failure_rolls_back_vocabulary_graph</c>
    /// to exercise the <c>CaptureAsync</c> rollback wiring inside
    /// <c>VocabularyService.SyncAsync</c> &mdash; the production
    /// TerminologyService swallows inner exceptions and surfaces them as
    /// <see cref="TerminologyResult.Error"/>, which makes the
    /// MarkError-on-exception path unreachable through the real
    /// implementation.
    /// </summary>
    private sealed class RollbackTerminologyFactory : AuthTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(ITerminologySync))
                    .ToList();
                foreach (var desc in descriptors) services.Remove(desc);
                services.AddSingleton<ITerminologySync, ThrowingTerminologySync>();
            });
        }
    }

    /// <summary>
    /// Stub ITerminologySync used by
    /// <see cref="RollbackTerminologyFactory"/>. Writes a single quad to
    /// the vocabulary graph (so the rollback has something to roll back)
    /// and then throws. Mirrors the partial-mutation-then-fail failure
    /// mode the production TerminologyService.SyncCore could exhibit if
    /// <c>_store.AddQuads</c> or one of its collaborators threw mid-loop.
    /// </summary>
    private sealed class ThrowingTerminologySync : ITerminologySync
    {
        private readonly StoreWrapper _store;

        public ThrowingTerminologySync(StoreWrapper store)
        {
            _store = store;
        }

        public TerminologyResult SyncAsync(KsContext ks, CancellationToken cancellationToken)
        {
            var graph = new Oxigraph.NamedNode(ks.VocabularyGraph);
            var marker = new Oxigraph.NamedNode(
                $"{ks.VocabularyGraph.TrimEnd('/')}/partial-mutation-marker");
            _store.AddQuads(graph, new[]
            {
                new Oxigraph.Quad(
                    marker,
                    new Oxigraph.NamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
                    new Oxigraph.NamedNode("http://example.org/test-marker"),
                    graph),
            });
            throw new InvalidOperationException(
                "test-forced terminology sync failure (rollback verification)");
        }
    }
}
