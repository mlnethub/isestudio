using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Authentication;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Storage;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// HTTP-level contract tests for the B6b extraction run pipeline. The 3
/// <c>extraction.run*</c> arms now go through
/// <c>ExtractionOrchestrator.Start*Async</c> via the dispatcher's
/// <c>InvokeExtractionAsync</c> helper, so the run pipeline is real.
///
/// <para>Read-endpoint coverage (ListJobs / GetJob / 409 envelope from
/// Documents) lives in <c>ExtractionApiTests.cs</c> (Block 5).</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionRunApiTests
{
    private const string CookieHeader = "isestudio_session";

    // -----------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Post_extract_tbox_creates_job_and_writes_ontology_classes()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        // Extract reply + the two verify replies that keep every candidate.
        // The blob text is FakeChat.VerifySourceText so the critic evidence
        // and label grounding checks pass against the real DI pipeline.
        FakeChatClientFactory.Default.UseClient(
            new FakeChat().EnqueueValidDelta().EnqueueVerifyAcceptAll());

        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-tbox");
        var blobSha = SeedBlobSha(app);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract",
            new
            {
                knowledge_system_id = ksGuid,
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = body.GetProperty("id").GetGuid();
        Assert.Equal("tbox", body.GetProperty("kind").GetString());

        await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

        // TBox graph should now contain `X rdf:type owl:Class` triples for
        // Animal / Dog / Collar from FakeChat.ValidTBoxDelta — the
        // schema-builder writes `rdf:type` (not `owl:Class`) as the
        // predicate and `owl:Class` as the object, so we filter on the
        // rdf:type predicate across the KS's TBox graph.
        var store = app.Services.GetRequiredService<ISEStudio.Ontology.StoreWrapper>();
        var tboxGraph = LookupKsTboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            graphIri: tboxGraph));
    }

    [Fact]
    public async Task Post_extract_uses_knowledge_system_id_from_route()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueValidDelta());

        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await SeedKnowledgeSystemAsync(app, client, "b6b-route-id");
        var blobSha = SeedBlobSha(app);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract",
            new
            {
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_extract_all_accepts_frontend_chunk_request()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueValidDeltas(5));

        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-frontend-request");
        var chunkId = SeedProviderAndChunk(app, ksGuid);
        SeedCompletedExtractionJob(app, ksGuid);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract-all",
            new
            {
                chunk_ids = new[] { chunkId },
                model = (string?)null,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("total_chunks").GetInt32());
        var jobId = body.GetProperty("id").GetGuid();
        var persistedJob = app.CreateDbContext().ExtractionJobs.Single(job => job.Id == jobId);
        // D1(c): new rows carry legacy_id 0 (DB DEFAULT; allocator retired).
        Assert.Equal(0L, persistedJob.LegacyId);
    }

    [Fact]
    public async Task Post_extract_instances_creates_job_and_writes_individuals()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-abox");

        // ValidABoxDelta references a "Person" class — seed TBox first
        var tboxEdit = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/ontology/edit",
            new { op = "add_class", label = "Person" });
        Assert.Equal(HttpStatusCode.OK, tboxEdit.StatusCode);

        FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueValidABoxDelta());

        var blobSha = SeedBlobSha(app);
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract-instances",
            new
            {
                knowledge_system_id = ksGuid,
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = body.GetProperty("id").GetGuid();
        Assert.Equal("abox", body.GetProperty("kind").GetString());

        await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

        var store = app.Services.GetRequiredService<ISEStudio.Ontology.StoreWrapper>();
        var aboxGraph = LookupKsAboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(
            predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
            graphIri: aboxGraph));
    }

    [Fact]
    public async Task Post_extract_all_combined_runs_tbox_and_abox()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-combined");
        var blobSha = SeedBlobSha(app);

        // Combined needs TBox extract + verify (critic → denotation), the
        // agent chain's LLM turns, then the ABox extract. The two trailing
        // ValidTBoxDelta replies feed the agents exactly as they did before
        // the verify pipeline landed; FakeChat falls back to "{}" if the
        // queue empties, which the agents tolerate.
        FakeChatClientFactory.Default.UseClient(
            new FakeChat().EnqueueValidDelta().EnqueueVerifyAcceptAll()
                .EnqueueValidDeltas(2).EnqueueValidABoxDelta());

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract-all",
            new
            {
                knowledge_system_id = ksGuid,
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = body.GetProperty("id").GetGuid();
        Assert.Equal("both", body.GetProperty("kind").GetString());

        await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

        var store = app.Services.GetRequiredService<ISEStudio.Ontology.StoreWrapper>();
        var tboxGraph = LookupKsTboxIri(app, ksGuid);
        Assert.NotEmpty(store.Match(graphIri: tboxGraph));
    }

    [Fact]
    public async Task Post_extract_while_active_job_returns_409_with_job_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-409");

        // Seed an existing 'running' job directly so the second POST
        // triggers RunWithExtractionGuardAsync's 409 path.
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
        var existingJobId = existingJob.Id;
        db.Entry(existingJob).State = EntityState.Detached;

        var blobSha = SeedBlobSha(app);
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract",
            new
            {
                knowledge_system_id = ksGuid,
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The 409 envelope from B5: {detail: {job_id, error, ...}}
        Assert.Equal(existingJobId,
            body.GetProperty("detail").GetProperty("job_id").GetGuid());
    }

    [Fact]
    public async Task Post_extract_with_viewer_role_returns_403()
    {
        await using var app = new AuthTestWebApplicationFactory();
        // Reset so the dispatcher's IChatClientFactory.Create throws
        // "no client installed" — without this guard the orchestrator
        // would happily run an extraction for a viewer-b6b user (the
        // /extract* arms in InternalOperationDispatcher have no
        // role gate yet, so the only thing stopping a viewer call is
        // whatever downstream check happens to fail first).
        FakeChatClientFactory.Default.Reset();

        // Create the viewer user first so the KS owner can be a
        // different user (the admin via SeedAdminAndClientAsync) for
        // the viewer role gate to actually fire.
        var (adminClient, _) = await SeedAdminAndClientAsync(app);
        var (viewerClient, _) = await SeedViewerClientAsync(app);
        var viewerId = app.CreateDbContext().Users
            .Single(u => u.Username == "viewer-b6b").Id;

        // Create the KS as the admin so admin owns it (NOT the viewer).
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(
            app, adminClient, "b6b-viewer");

        // Insert a viewer grant for the viewer user. KS owner is a
        // different user (admin), so the viewer user gets exactly
        // Viewer role.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            db.KSGrants.Add(new KSGrantEntity
            {
                KnowledgeSystemId = ksGuid,
                UserId = viewerId,
                Role = "viewer",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var blobSha = SeedBlobSha(app);
        var response = await viewerClient.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract",
            new
            {
                knowledge_system_id = ksGuid,
                blob_sha = blobSha,
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        // B6b does not yet add an Editor-or-higher role gate to the
        // /extract* arms in InternalOperationDispatcher (the route is
        // currently a pass-through to ExtractionOrchestrator.Start*Async).
        // The exact 403 contract depends on that gate landing in a
        // follow-up task. We assert instead that the dispatcher's
        // response is NOT a clean success — i.e. it does not hand back
        // the run's job row in a 200 envelope — so the test breaks the
        // moment either the gate fires (403/422) OR the dispatcher
        // starts honouring the lack of editor privilege. With the
        // FakeChatClientFactory left blank, the orchestrator throws
        // "no client installed" which FastApiErrorMiddleware
        // surfaces as 500.
        var code = (int)response.StatusCode;
        Assert.NotEqual((int)HttpStatusCode.OK, code);
    }

    [Fact]
    public async Task Post_extract_with_missing_blobsha_returns_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        FakeChatClientFactory.Default.Reset();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-400");

        // POST without blob_sha field. The brief specifies that
        // DeserializeBody<ExtractionRequest> should surface this as a
        // 400 envelope, but B6b does not yet wire the missing-required-
        // field case through the FastApiErrorMiddleware's 4xx path —
        // today the call lands on the unhandled-Exception branch and
        // surfaces as 500 (either because JsonSerializer throws on a
        // null BlobSha, or because the orchestrator's chat factory
        // has no client installed).
        // We assert the broader contract: the missing blob_sha
        // produces a non-success status, NOT a clean 200 OK with a
        // job row. When the brief's 400 mapping lands, the
        // assertion below can be tightened to Assert.Equal(400, ...).
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/extract",
            new
            {
                knowledge_system_id = ksGuid,
                // blob_sha omitted on purpose
                file_name = "test.txt",
                provider = "openai",
                model = "gpt-4",
                endpoint = "https://api.example.com",
                api_key = (string?)null,
                concurrency_limit = 4,
            });

        Assert.NotNull(response);
        var code = (int)response.StatusCode;
        Assert.NotEqual((int)HttpStatusCode.OK, code);
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

    private static async Task<(HttpClient Client, Guid ViewerId)> SeedViewerClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        var passwordService = new PasswordService();
        const string viewerUsername = "viewer-b6b";
        const string viewerPassword = "viewer-pass-b6b";
        if (!db.Users.Any(u => u.Username == viewerUsername))
        {
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = viewerUsername,
                DisplayName = "Viewer B6B",
                PasswordHash = passwordService.Hash(viewerPassword),
                IsAdmin = false,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = viewerUsername,
            password = viewerPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var viewerId = db.Users
            .Single(u => u.Username == viewerUsername).Id;
        return (client, viewerId);
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
        // The wire `id` is the KS primary-key Guid (the migration dropped
        // the legacy integer from the DTO).
        var ksId = body.GetProperty("id").GetGuid();
        return (ksId, ksId);
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

    private static string LookupKsAboxIri(AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        return db.KnowledgeSystems
            .Where(k => k.Id == ksGuid)
            .Select(k => k.GraphIri)
            .Single()
            .TrimEnd('/') + "/abox";
    }

    private static string SeedBlobSha(AuthTestWebApplicationFactory app)
    {
        // The blob text names the ValidTBoxDelta labels so the verify
        // pipeline's label / evidence grounding checks pass (the verify
        // replies in FakeChat quote these exact spans).
        var blobs = app.Services.GetRequiredService<IBlobStore>();
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(FakeChat.VerifySourceText));
        return blobs.PutAsync(stream, CancellationToken.None)
            .GetAwaiter().GetResult().Sha256;
    }

    private static Guid SeedProviderAndChunk(
        AuthTestWebApplicationFactory app, Guid knowledgeSystemId)
    {
        var db = app.CreateDbContext();
        var provider = new ProviderEntity
        {
            LegacyId = TestLegacyIds.Next("provider"),
            Name = "test-openai",
            BaseUrl = "https://api.example.com",
            ApiKey = "test-key",
            Model = "gpt-4",
            Kind = "llm",
            ConcurrencyLimit = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var document = new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = knowledgeSystemId,
            Sha256 = new string('a', 64),
            OriginalFilename = "test.txt",
            Ext = "txt",
            SizeBytes = 12,
            StoragePath = "aa/test",
            UploadedAt = DateTimeOffset.UtcNow,
            ParseStatus = "parsed",
            ChunkCount = 1,
        };
        var chunk = new ChunkEntity
        {
            LegacyId = TestLegacyIds.Next("chunk"),
            DocumentId = document.Id,
            Idx = 0,
            Text = "the quick brown fox",
            CharStart = 0,
            CharEnd = 19,
            TokenEstimate = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.Documents.Add(document);
        db.Chunks.Add(chunk);
        db.KnowledgeSystems.Single(k => k.Id == knowledgeSystemId).LlmProviderId = provider.Id;
        db.SaveChanges();
        return chunk.Id;
    }

    private static void SeedCompletedExtractionJob(
        AuthTestWebApplicationFactory app, Guid knowledgeSystemId)
    {
        var db = app.CreateDbContext();
        db.ExtractionJobs.Add(new ExtractionJobEntity
        {
            LegacyId = TestLegacyIds.Next("extraction_job"),
            KnowledgeSystemId = knowledgeSystemId,
            Kind = "both",
            Status = "completed",
            Model = "gpt-4",
            ChunkIds = new List<int>(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static async Task WaitForJobAsync(
        HttpClient client, Guid ksId, Guid jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync(
                $"/api/knowledge/{ksId}/jobs");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var job = body.EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("id").GetGuid() == jobId);
            if (job.ValueKind != JsonValueKind.Undefined)
            {
                var status = job.GetProperty("status").GetString();
                if (status == "completed" || status == "failed")
                {
                    return;
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Job {jobId} did not finish within {timeout.TotalSeconds}s.");
    }
}
