using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/jobs*</c> and
/// the 409 envelope that the document slice raises when an extraction
/// is in flight for the same KS.
/// <list type="bullet">
///   <item><description>Real Kestrel via <see cref="AuthTestWebApplicationFactory"/>.</description></item>
///   <item><description>SQLite + per-test temp blob root so concurrent
///   tests don't share disk state.</description></item>
///   <item><description>ExtractionJob rows are seeded directly against
///   the EF Core context — no orchestrator / LLM is invoked, the slice
///   we're covering here is the read endpoints and the 409 envelope,
///   not the run pipeline (the run pipeline still owns Block 6).</description></item>
/// </list>
/// </summary>
public sealed class ExtractionApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // -----------------------------------------------------------------
    // Jobs read endpoints
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListJobs_returns_empty_when_no_jobs()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "jobs-empty");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task ListJobs_returns_seeded_jobs_newest_first()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "jobs-populated");

        SeedExtractionJob(app, ksGuid, kind: "tbox", status: "completed", model: "gpt-x");
        SeedExtractionJob(app, ksGuid, kind: "abox", status: "running", model: "gpt-y");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetArrayLength());

        var first = body[0];
        var second = body[1];
        Assert.Equal("running", first.GetProperty("status").GetString());
        Assert.Equal("abox", first.GetProperty("kind").GetString());
        Assert.Equal("gpt-y", first.GetProperty("model").GetString());
        Assert.Equal("completed", second.GetProperty("status").GetString());
        Assert.Equal("tbox", second.GetProperty("kind").GetString());
        Assert.Equal("gpt-x", second.GetProperty("model").GetString());

        // Newest-first ordering is asserted via the created_at column
        // the seed stamps deterministically: the row we seeded second
        // must come first.
        var firstCreated = first.GetProperty("created_at").GetDateTimeOffset();
        var secondCreated = second.GetProperty("created_at").GetDateTimeOffset();
        Assert.True(firstCreated >= secondCreated);
    }

    [Fact]
    public async Task ListJobs_scoped_to_knowledge_system()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ks1, ks1Guid) = await CreateKsAsync(app, client, "jobs-ks1");
        var (ks2, ks2Guid) = await CreateKsAsync(app, client, "jobs-ks2");

        SeedExtractionJob(app, ks1Guid, kind: "tbox", status: "pending", model: "m1");
        SeedExtractionJob(app, ks2Guid, kind: "tbox", status: "pending", model: "m2");
        SeedExtractionJob(app, ks2Guid, kind: "tbox", status: "pending", model: "m3");

        var r1 = await client.GetAsync($"/api/knowledge/{ks1}/jobs");
        var r2 = await client.GetAsync($"/api/knowledge/{ks2}/jobs");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var b1 = await r1.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var b2 = await r2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, b1.GetArrayLength());
        Assert.Equal(2, b2.GetArrayLength());
        Assert.Equal("m1", b1[0].GetProperty("model").GetString());
    }

    [Fact]
    public async Task GetJob_returns_seeded_job_with_full_wire_shape()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "jobs-get");
        var jobId = SeedExtractionJob(
            app, ksGuid, kind: "tbox", status: "completed", model: "test-model",
            totalChunks: 4, processedChunks: 4, axioms_added: 7);

        var response = await client.GetAsync($"/api/knowledge/{ksId}/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(jobId, body.GetProperty("id").GetGuid());
        Assert.Equal(ksGuid, body.GetProperty("knowledge_system_id").GetGuid());
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal("tbox", body.GetProperty("kind").GetString());
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(4, body.GetProperty("total_chunks").GetInt32());
        Assert.Equal(4, body.GetProperty("processed_chunks").GetInt32());
        Assert.Equal(7, body.GetProperty("axioms_added").GetInt32());
        Assert.Equal("[]", body.GetProperty("chunk_ids").GetRawText());
        Assert.Equal("", body.GetProperty("log").GetString());
        Assert.Equal(0, body.GetProperty("terms_added").GetInt32());
    }

    [Fact]
    public async Task GetJob_returns_placeholder_when_missing()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "jobs-missing");

        var response = await client.GetAsync(
            $"/api/knowledge/{ksId}/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // The dispatcher collapses unknown job ids into the documented
        // empty placeholder shape (matches the python parity contract
        // that surfaces a 404 on the same path).
        Assert.Equal(Guid.Empty, body.GetProperty("id").GetGuid());
    }

    // -----------------------------------------------------------------
    // 409 envelope
    // -----------------------------------------------------------------

    [Fact]
    public async Task Documents_delete_returns_409_with_job_id_when_active_job()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "conflict-delete");
        var upload = await UploadAsync(client, ksId, "x.txt", "x\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetGuid();

        var jobId = SeedExtractionJob(app, ksGuid, kind: "tbox", status: "running", model: "x");

        var response = await client.PostAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/delete", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(jobId, body.GetProperty("detail").GetProperty("job_id").GetGuid());
        Assert.False(string.IsNullOrEmpty(
            body.GetProperty("detail").GetProperty("error").GetString()));

        // The document row was NOT deleted — the guard short-circuited.
        var stillThere = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task Documents_parse_returns_409_with_job_id_when_active_job()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "conflict-parse");
        var upload = await UploadAsync(client, ksId, "y.txt", "y\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetGuid();

        var jobId = SeedExtractionJob(app, ksGuid, kind: "abox", status: "pending", model: "y");

        var response = await client.PostAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(jobId, body.GetProperty("detail").GetProperty("job_id").GetGuid());

        // The doc was not parsed — its parse_status stays at the
        // upload-time "pending".
        var get = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}");
        var fetched = await get.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("pending", fetched.GetProperty("parse_status").GetString());
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static Guid SeedExtractionJob(
        AuthTestWebApplicationFactory app,
        Guid ksGuid,
        string kind,
        string status,
        string model,
        int totalChunks = 0,
        int processedChunks = 0,
        int axioms_added = 0)
    {
        // Each test gets its own factory (and therefore its own SQLite
        // file), so the legacy-id allocator doesn't need a per-table
        // prefix to stay unique. We still go through TestLegacyIds so
        // the index matches what production migrations expect.
        var db = app.CreateDbContext();
        var job = new ExtractionJobEntity
        {
            LegacyId = TestLegacyIds.Next("extraction_job"),
            KnowledgeSystemId = ksGuid,
            Kind = kind,
            Status = status,
            Model = model,
            ChunkIds = new List<int>(),
            TotalChunks = totalChunks,
            ProcessedChunks = processedChunks,
            AxiomsAdded = axioms_added,
            CreatedAt = DateTimeOffset.UtcNow,
            Log = string.Empty,
            Phase = string.Empty,
        };
        db.ExtractionJobs.Add(job);
        db.SaveChanges();
        // Round-trip the Guid out of the test database so the caller
        // can build the route path; EF Core sets it on SaveChanges().
        db.Entry(job).State = EntityState.Detached;
        return job.Id;
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
        var adminId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<(Guid KsId, Guid KsGuid)> CreateKsAsync(
        AuthTestWebApplicationFactory app, HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // The wire `id` is the KS primary-key Guid (the migration dropped
        // the legacy integer from the DTO).
        var ksId = body.GetProperty("id").GetGuid();
        return (ksId, ksId);
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