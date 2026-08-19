using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Documents;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/documents*</c> &mdash;
/// upload / list / get / parse / chunks / move / delete / contribution / impact.
/// Mirrors the established <see cref="Knowledge.KnowledgeApiTests"/> pattern:
/// <list type="bullet">
///   <item><description>Real Kestrel via <see cref="AuthTestWebApplicationFactory"/>.</description></item>
///   <item><description>SQLite + per-test temp blob root so concurrent tests
///   don't share disk state.</description></item>
///   <item><description>The <c>IDocumentParser</c> is swapped for
///   <see cref="TestDocumentParser"/> so parse tests don't depend on
///   DoclingDotNet / PdfPig native binaries.</description></item>
///   <item><description>Raw <c>HttpClient</c> + <c>JsonElement</c> parsing so the
///   tests stay tolerant of harmless extra fields.</description></item>
/// </list>
/// </summary>
public sealed class DocumentApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // -----------------------------------------------------------------
    // List / get
    // -----------------------------------------------------------------

    [Fact]
    public async Task List_returns_empty_when_no_documents()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "list-empty");

        var response = await client.GetAsync($"/api/knowledge/{ksId}/documents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Upload_text_then_list_returns_it()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "upload-list");

        var upload = await UploadAsync(client, ksId, "notes.txt", "hello world\n",
            folder: "/papers");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var doc = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("notes.txt", doc.GetProperty("original_filename").GetString());
        Assert.Equal("/papers", doc.GetProperty("folder").GetString());
        Assert.Equal("pending", doc.GetProperty("parse_status").GetString());

        var list = await client.GetAsync($"/api/knowledge/{ksId}/documents");
        var body = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
    }

    [Fact]
    public async Task Upload_dedup_within_ks_collapses_to_one_row()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "dedup");

        var bytes = "the same bytes again\n"u8.ToArray();
        var first = await UploadBytesAsync(client, ksId, "first.txt", bytes, folder: "/a");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDoc = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var firstId = firstDoc.GetProperty("id").GetInt64();

        var second = await UploadBytesAsync(client, ksId, "second.txt", bytes, folder: "/b");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondDoc = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // Same row (same sha + same ks), just moved to /b.
        Assert.Equal(firstId, secondDoc.GetProperty("id").GetInt64());
        Assert.Equal("/b", secondDoc.GetProperty("folder").GetString());

        var list = await client.GetAsync($"/api/knowledge/{ksId}/documents");
        var body = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
    }

    [Fact]
    public async Task Upload_same_bytes_to_two_ks_creates_two_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ks1, _) = await CreateKsAsync(app, client, "ks-a");
        var (ks2, _) = await CreateKsAsync(app, client, "ks-b");

        var bytes = "shared content\n"u8.ToArray();
        var first = await UploadBytesAsync(client, ks1, "shared.txt", bytes, folder: "/");
        var second = await UploadBytesAsync(client, ks2, "shared.txt", bytes, folder: "/");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstDoc = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var secondDoc = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // Per-KS dedup is scoped to the KS, not global — same sha produces
        // two distinct document rows.
        Assert.NotEqual(
            firstDoc.GetProperty("id").GetInt64(),
            secondDoc.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Upload_unsupported_extension_returns_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "bad-ext");

        var response = await UploadBytesAsync(client, ksId, "slides.pptx",
            "fake content\n"u8.ToArray(), folder: "/");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_empty_file_returns_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "empty");

        var response = await UploadBytesAsync(client, ksId, "empty.txt",
            Array.Empty<byte>(), folder: "/");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_returns_doc_by_id()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "get");
        var upload = await UploadAsync(client, ksId, "thing.md", "# hello\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var get = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(docId, body.GetProperty("id").GetInt64());
        Assert.Equal("thing.md", body.GetProperty("original_filename").GetString());
    }

    [Fact]
    public async Task Get_returns_other_ks_doc_as_not_found()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ks1, _) = await CreateKsAsync(app, client, "owner");
        var (ks2, _) = await CreateKsAsync(app, client, "stranger");
        var upload = await UploadAsync(client, ks1, "private.txt", "secret\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        // Cross-KS lookup: the doc exists but belongs to ks1 — the role
        // gate (Viewer on ks2) denies and the dispatcher returns the
        // empty placeholder shape (consistent with the rest of the
        // dispatcher surface; Python returns 404 here).
        var cross = await client.GetAsync($"/api/knowledge/{ks2}/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, cross.StatusCode);
        var body = await cross.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0L, body.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ListPage_filters_by_folder_and_status()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "filters");
        await UploadAsync(client, ksId, "a.txt", "alpha\n", folder: "/x");
        await UploadAsync(client, ksId, "b.txt", "beta\n", folder: "/y");
        await UploadAsync(client, ksId, "c.txt", "gamma\n", folder: "/x");

        var page = await client.GetAsync(
            $"/api/knowledge/{ksId}/documents/page?folder=/x&limit=10");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        Assert.Equal(2L, body.GetProperty("total").GetInt64());
        var folders = body.GetProperty("folders").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("/x", folders);
        Assert.Contains("/y", folders);
    }

    // -----------------------------------------------------------------
    // Parse flow
    // -----------------------------------------------------------------

    [Fact]
    public async Task Parse_extracts_text_and_creates_chunks()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "parse-ok");
        var upload = await UploadAsync(client, ksId, "doc.txt",
            "line one\n\nline two with enough text to exceed floor\n\nline three\n",
            folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var parse = await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parse.StatusCode);
        var parsed = await parse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("parsed", parsed.GetProperty("parse_status").GetString());
        Assert.True(parsed.GetProperty("chunk_count").GetInt32() > 0);

        var chunks = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}/chunks");
        var chunkBody = await chunks.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(chunkBody.GetArrayLength() > 0);
        // Verify monotonic Idx ordering.
        var idxs = chunkBody.EnumerateArray().Select(c => c.GetProperty("idx").GetInt32()).ToList();
        Assert.Equal(idxs.OrderBy(i => i).ToList(), idxs);
    }

    [Fact]
    public async Task Parse_idempotent_replaces_chunks()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "reparse");
        var upload = await UploadAsync(client, ksId, "reparse.txt",
            "first parse content\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        var first = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}/chunks");
        var firstBody = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var firstCount = firstBody.GetArrayLength();

        // Re-parse; the service should drop + recreate (idempotent).
        await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        var second = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}/chunks");
        var secondBody = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(firstCount, secondBody.GetArrayLength());
    }

    [Fact]
    public async Task Parse_failed_records_error_status()
    {
        await using var app = new ThrowingParserFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "fail");
        var upload = await UploadAsync(client, ksId, "broken.txt", "any content\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var parse = await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parse.StatusCode);
        var body = await parse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("failed", body.GetProperty("parse_status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ParseBatch_parses_selected_ids()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "batch-ids");
        var up1 = await UploadAsync(client, ksId, "a.txt", "alpha\n", folder: "/");
        var up2 = await UploadAsync(client, ksId, "b.txt", "beta\n", folder: "/");
        var id1 = (await up1.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetInt64();
        var id2 = (await up2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetInt64();

        var batch = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/documents/parse-batch",
            new { document_ids = new[] { id1, id2 } });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var body = await batch.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("parsed").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task ParseBatch_with_folders_recursive()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "batch-folders");
        await UploadAsync(client, ksId, "root.txt", "root\n", folder: "/manuals");
        await UploadAsync(client, ksId, "nested.txt", "nested\n", folder: "/manuals/pumps");

        var batch = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/documents/parse-batch",
            new { folders = new[] { "/manuals" }, recursive = true });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var body = await batch.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
    }

    // -----------------------------------------------------------------
    // Chunks + contribution + impact
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListChunks_returns_empty_before_parse()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "no-chunks");
        var upload = await UploadAsync(client, ksId, "u.txt", "u\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var chunks = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}/chunks");
        Assert.Equal(HttpStatusCode.OK, chunks.StatusCode);
        var body = await chunks.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Contribution_returns_zero_when_no_provenance()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "contrib-zero");
        var upload = await UploadAsync(client, ksId, "c.txt", "c\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var contrib = await client.GetAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/contribution");
        Assert.Equal(HttpStatusCode.OK, contrib.StatusCode);
        var body = await contrib.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetProperty("axiom_count").GetInt32());
        Assert.Equal(0, body.GetProperty("individual_count").GetInt32());
    }

    [Fact]
    public async Task Contribution_counts_distinct_axioms_and_individuals()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "contrib");
        var upload = await UploadAsync(client, ksId, "d.txt", "d\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();
        var parse = await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parse.StatusCode);

        // Contribution joins provenance rows to this document's chunk
        // ids, so the seeded rows must point at a real chunk.
        var chunkId = LookupFirstChunkId(app, ksGuid);

        SeedAxiomProvenance(app, ksGuid, chunkId, "subClassOf|dog|animal");
        SeedAxiomProvenance(app, ksGuid, chunkId, "subClassOf|cat|animal");
        SeedEntityResolution(app, ksGuid, chunkId, "http://ex/Ind#1");
        SeedEntityResolution(app, ksGuid, chunkId, "http://ex/Ind#1"); // duplicate
        SeedEntityResolution(app, ksGuid, chunkId, "http://ex/Ind#2");

        var contrib = await client.GetAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/contribution");
        var body = await contrib.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("axiom_count").GetInt32());
        Assert.Equal(2, body.GetProperty("individual_count").GetInt32());
    }

    [Fact]
    public async Task Impact_returns_empty_placeholder()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "impact");
        var upload = await UploadAsync(client, ksId, "i.txt", "i\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var impact = await client.GetAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/impact");
        Assert.Equal(HttpStatusCode.OK, impact.StatusCode);
        var body = await impact.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(docId, body.GetProperty("document_id").GetInt64());
        Assert.Equal(0, body.GetProperty("systems").GetArrayLength());
    }

    // -----------------------------------------------------------------
    // Move / delete
    // -----------------------------------------------------------------

    [Fact]
    public async Task Move_updates_folder_and_filename()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "move");
        var upload = await UploadAsync(client, ksId, "old.txt", "x\n", folder: "/a");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        var patch = await client.PatchAsJsonAsync(
            $"/api/knowledge/{ksId}/documents/{docId}",
            new { folder = "/b", original_filename = "new.txt" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = await patch.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("/b", body.GetProperty("folder").GetString());
        Assert.Equal("new.txt", body.GetProperty("original_filename").GetString());
    }

    [Fact]
    public async Task Delete_removes_doc_and_chunks_and_blob()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "delete-orphan");
        var upload = await UploadAsync(client, ksId, "ephemeral.txt", "ephemeral\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();
        await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);

        var delete = await client.PostAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var delBody = await delete.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(delBody.GetProperty("ok").GetBoolean());

        var list = await client.GetAsync($"/api/knowledge/{ksId}/documents");
        var listBody = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, listBody.GetArrayLength());

        // Confirm in DB that chunks are gone.
        var db = app.CreateDbContext();
        Assert.Empty(db.Chunks);
    }

    [Fact]
    public async Task Delete_keeps_blob_when_other_doc_references_sha()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ks1, _) = await CreateKsAsync(app, client, "ref-1");
        var (ks2, _) = await CreateKsAsync(app, client, "ref-2");
        var bytes = "shared bytes\n"u8.ToArray();
        var up1 = await UploadBytesAsync(client, ks1, "shared.txt", bytes, folder: "/");
        var up2 = await UploadBytesAsync(client, ks2, "shared.txt", bytes, folder: "/");
        var doc1 = (await up1.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetInt64();
        var doc2 = (await up2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetInt64();

        // Delete doc1; doc2 still references the same sha so the blob
        // store should NOT have orphaned it.
        var delete = await client.PostAsync(
            $"/api/knowledge/{ks1}/documents/{doc1}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.True((await delete.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("ok").GetBoolean());

        // Confirm doc2 is still readable (blob not removed).
        var get2 = await client.GetAsync($"/api/knowledge/{ks2}/documents/{doc2}");
        Assert.Equal(HttpStatusCode.OK, get2.StatusCode);
        var body2 = await get2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(doc2, body2.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Delete_cascades_provenance_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await CreateKsAsync(app, client, "cascade");
        var upload = await UploadAsync(client, ksId, "c.txt", "c\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();
        await client.PostAsync($"/api/knowledge/{ksId}/documents/{docId}/parse", null);

        var chunkId = LookupFirstChunkId(app, ksGuid);
        SeedAxiomProvenance(app, ksGuid, chunkId, "subClassOf|a|b");
        SeedEntityResolution(app, ksGuid, chunkId, "http://ex/Ind#cascade");

        var dbBefore = app.CreateDbContext();
        Assert.True(dbBefore.AxiomProvenances.Any(p => p.KnowledgeSystemId == ksGuid));

        var delete = await client.PostAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var dbAfter = app.CreateDbContext();
        Assert.False(dbAfter.AxiomProvenances.Any(p => p.KnowledgeSystemId == ksGuid));
        Assert.False(dbAfter.EntityResolutions.Any(r => r.KnowledgeSystemId == ksGuid));
    }

    [Fact]
    public async Task Delete_returns_not_found_when_doc_missing()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "missing");

        var delete = await client.PostAsync(
            $"/api/knowledge/{ksId}/documents/99999/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var body = await delete.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Delete_without_writer_role_returns_not_found()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, _) = await CreateKsAsync(app, client, "no-role");

        // Upload a doc as admin.
        var upload = await UploadAsync(client, ksId, "r.txt", "r\n", folder: "/");
        var created = await upload.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var docId = created.GetProperty("id").GetInt64();

        // Switch to a non-admin, non-grantee user (no role on this KS).
        var (aliceClient, _) = await LoginAliceAsync(app);

        var delete = await aliceClient.PostAsync(
            $"/api/knowledge/{ksId}/documents/{docId}/delete", null);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var body = await delete.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());

        // Verify the doc is still present.
        var still = await client.GetAsync($"/api/knowledge/{ksId}/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
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
            db.Users.Add(new OnToPilot.Infrastructure.Persistence.Entities.UserEntity
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

    private static async Task<(HttpClient Client, Guid UserId)> LoginAliceAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.OtherUsername))
        {
            var passwordService = new OnToPilot.Authentication.PasswordService();
            db.Users.Add(new OnToPilot.Infrastructure.Persistence.Entities.UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = AuthTestWebApplicationFactory.OtherUsername,
                DisplayName = AuthTestWebApplicationFactory.OtherUsername,
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
        var userId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.OtherUsername).Id;
        return (client, userId);
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
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
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

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, long ksId, string fileName, string content, string folder)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await UploadBytesAsync(client, ksId, fileName, bytes, folder);
    }

    private static async Task<HttpResponseMessage> UploadBytesAsync(
        HttpClient client, long ksId, string fileName, byte[] bytes, string folder)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", fileName },
            { new StringContent(folder), "folder" },
        };
        return await client.PostAsync($"/api/knowledge/{ksId}/documents/upload", content);
    }

    private static void SeedAxiomProvenance(
        AuthTestWebApplicationFactory app, Guid ksGuid, Guid? chunkId, string axiomKey)
    {
        var db = app.CreateDbContext();
        db.AxiomProvenances.Add(new OnToPilot.Infrastructure.Persistence.Entities.AxiomProvenanceEntity
        {
            LegacyId = TestLegacyIds.Next("axiom_provenance"),
            KnowledgeSystemId = ksGuid,
            ChunkId = chunkId,
            AxiomKey = axiomKey,
            Method = "seed",
            ActorName = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static void SeedEntityResolution(
        AuthTestWebApplicationFactory app, Guid ksGuid, Guid? sourceChunkId, string iri)
    {
        var db = app.CreateDbContext();
        db.EntityResolutions.Add(new OnToPilot.Infrastructure.Persistence.Entities.EntityResolutionEntity
        {
            LegacyId = TestLegacyIds.Next("entity_resolution"),
            KnowledgeSystemId = ksGuid,
            SourceChunkId = sourceChunkId,
            IndividualIri = iri,
            Status = "new",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Look up the Guid primary key of the first chunk in the only
    /// document under this KS. Used by the contribution test to seed
    /// provenance rows that the service can join back to the doc's
    /// chunks.
    /// </summary>
    private static Guid LookupFirstChunkId(AuthTestWebApplicationFactory app, Guid ksGuid)
    {
        var db = app.CreateDbContext();
        // SQLite refuses DateTimeOffset in ORDER BY; materialise + sort
        // client-side (same workaround as KnowledgeService.ListAsync).
        var docId = db.Documents
            .Where(d => d.KnowledgeSystemId == ksGuid)
            .ToList()
            .OrderBy(d => d.UploadedAt)
            .Select(d => d.Id)
            .First();
        return db.Chunks
            .Where(c => c.DocumentId == docId)
            .OrderBy(c => c.Idx)
            .Select(c => c.Id)
            .First();
    }

    /// <summary>
    /// Test-only <see cref="OnToPilot.Parsing.IDocumentParser"/> that
    /// throws on every call so the parse-failed-status test can exercise
    /// the failure branch without depending on a particular binary
    /// format.
    /// </summary>
    private sealed class ThrowingTestParser : OnToPilot.Parsing.IDocumentParser
    {
        public OnToPilot.Parsing.ParseResult Parse(Stream content, string fileName)
        {
            throw new InvalidOperationException("forced test failure");
        }
    }

    /// <summary>
    /// Variant of <see cref="AuthTestWebApplicationFactory"/> that
    /// substitutes a parser that throws on every call so the
    /// <c>Parse_failed_records_error_status</c> test can drive the
    /// failure branch.
    /// </summary>
    private sealed class ThrowingParserFactory : AuthTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(OnToPilot.Parsing.IDocumentParser))
                    .ToList();
                foreach (var d in descriptors) services.Remove(d);
                services.AddSingleton<OnToPilot.Parsing.IDocumentParser, ThrowingTestParser>();
            });
        }
    }
}