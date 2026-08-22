using System.Net;
using System.Net.Http.Json;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Conflicts;

/// <summary>
/// HTTP-level contract tests for <c>/api/knowledge/{ks_id}/conflicts*</c>
/// and <c>/api/knowledge/{ks_id}/reconciliations*</c>. Mirrors
/// <see cref="OnToPilot.Tests.Providers.ProvidersApiTests"/>:
/// <list type="bullet">
///   <item><description>Real Kestrel via <see cref="AuthTestWebApplicationFactory"/>.</description></item>
///   <item><description>SQLite, per-test database, admin user seeded in-line.</description></item>
///   <item><description>Raw <c>HttpClient</c>; the SQL-side paths are the
///   contract; the structural detection algorithm itself is covered by the
///   pure unit tests under <c>Ontology/ConflictDetectionTests</c> (no Oxigraph).</description></item>
/// </list>
/// </summary>
public sealed class ConflictApiTests
{
    private const string CookieHeader = "ontopilot_session";

    [Fact]
    public async Task List_returns_empty_when_no_conflicts()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "list-empty");

        var response = await client.GetAsync($"/api/knowledge/{ks.Id}/conflicts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(System.Text.Json.JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Fact]
    public async Task List_with_status_all_returns_dismissed()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "list-all");
        SeedConflict(app, ks.Id, status: "dismissed", ctype: "duplicate");

        var response = await client.GetAsync($"/api/knowledge/{ks.Id}/conflicts?status=all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("dismissed", rows[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_filters_by_ctype()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "filter-ctype");
        SeedConflict(app, ks.Id, status: "open", ctype: "cycle");
        SeedConflict(app, ks.Id, status: "open", ctype: "duplicate");

        var response = await client.GetAsync($"/api/knowledge/{ks.Id}/conflicts?ctype=cycle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("cycle", rows[0].GetProperty("ctype").GetString());
    }

    [Fact]
    public async Task Detect_with_empty_graph_auto_clears_stale_open_rows()
    {
        // The Block 6 wiring gives the SQLite test factory a per-test
        // Oxigraph handle, so the detector now runs against the live
        // graph even in the contract-test path. With an empty graph
        // the detector emits no signatures, so the existing open rows
        // sync to "resolved" / "auto-cleared" while dismissed rows
        // stay untouched (the user signalled they were noise).
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "detect-empty");
        SeedConflict(app, ks.Id, status: "open", ctype: "cycle", signature: "cycle|A|B|C");
        SeedConflict(app, ks.Id, status: "dismissed", ctype: "duplicate", signature: "dup|X|Y");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/conflicts/detect", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, rows.GetArrayLength());

        // The previously-dismissed row kept its status — the detector
        // never re-promotes a manually-dismissed conflict into a fresh
        // auto-cleared resolution (the user's earlier judgment wins).
        var dismissed = await client.GetAsync(
            $"/api/knowledge/{ks.Id}/conflicts?status=dismissed");
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        var dismissedRows = await dismissed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, dismissedRows.GetArrayLength());
        Assert.Equal("dismissed", dismissedRows[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Detect_accepts_post_with_no_body()
    {
        // Python baseline: POST /conflicts/detect takes no body
        // (backend/app/api/conflicts.py:137 — signature is just the path
        // param + deps). The C# port previously declared [FromBody] object
        // body, which made ASP.NET Core return 415 when the frontend POSTed
        // with no body / no content-type (the natural shape for a body-less
        // mutation). Mirrors the no-body dismiss/reopen pattern.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "detect-nobody");

        var response = await client.PostAsync(
            $"/api/knowledge/{ks.Id}/conflicts/detect", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_returns_200_and_null_view_not_500()
    {
        // ResolveConflictResponse.View was typed as a non-nullable JsonElement
        // and stubbed with `default` (an uninitialized struct). Serialising a
        // default JsonElement throws InvalidOperationException ("Operation is
        // not valid due to the current state of the object") inside
        // JsonElementConverter.Write, surfacing as HTTP 500 on a successful
        // resolve. The frontend ignores `view` (it refreshes separately), so
        // the stub should serialise as JSON null. Seed a conflict with a
        // no-op resolution so no ontology edit is required.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "resolve-view");
        var db = app.CreateDbContext();
        var cid = Guid.NewGuid();
        db.Conflicts.Add(new ConflictEntity
        {
            Id = cid,
            LegacyId = TestLegacyIds.Next("conflict"),
            KnowledgeSystemId = ks.Id,
            Signature = "cycle|A|B|C",
            Ctype = "cycle",
            Severity = "error",
            Status = "open",
            Title = "cycle conflict (resolve)",
            Detail = "Seeded for resolve test.",
            Payload = System.Text.Json.JsonDocument.Parse(
                """{"entities":[],"resolutions":[{"id":"keep-a","label":"Keep A","op":{"op":"noop"}}]}"""),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/conflicts/{cid}/resolve",
            new { resolution_id = "keep-a" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.TryGetProperty("view", out var viewProp));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, viewProp.ValueKind);
    }

    [Fact]
    public async Task Resolve_set_property_union_with_array_members_does_not_500()
    {
        // Regression: ConflictService.ReadResolutions → JsonElementToObject
        // converted JSON arrays to raw text (GetRawText), so the members
        // array arrived as a string like "["iri1","iri2"]" instead of a
        // List<object?>. OntologyEditor.ReadStringArray couldn't parse it,
        // SetPropertyUnion threw "union needs at least two members" → 500.
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "resolve-union");
        var db = app.CreateDbContext();
        var cid = Guid.NewGuid();
        db.Conflicts.Add(new ConflictEntity
        {
            Id = cid,
            LegacyId = TestLegacyIds.Next("conflict"),
            KnowledgeSystemId = ks.Id,
            Signature = "range_multi|http://test/prop",
            Ctype = "range_multi",
            Severity = "warning",
            Status = "open",
            Title = "Range conflict (resolve-union)",
            Detail = "Seeded for union resolve test.",
            Payload = System.Text.Json.JsonDocument.Parse(
                """{"entities":[],"resolutions":[{"id":"union","label":"Use union range","op":{"op":"set_property_union","iri":"http://test/prop","slot":"range","members":["http://test/A","http://test/B"]}}]}"""),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ks.Id}/conflicts/{cid}/resolve",
            new { resolution_id = "union" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dismiss_then_reopen_flips_status()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "dismiss-reopen");
        var conflictId = SeedConflict(app, ks.Id, status: "open", ctype: "domain_multi");

        var dismissResponse = await client.PostAsync(
            $"/api/knowledge/{ks.Id}/conflicts/{conflictId}/dismiss", content: null);
        Assert.Equal(HttpStatusCode.OK, dismissResponse.StatusCode);
        var dismissed = await dismissResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("dismissed", dismissed.GetProperty("status").GetString());
        Assert.Equal("dismissed", dismissed.GetProperty("resolution").GetString());

        var reopenResponse = await client.PostAsync(
            $"/api/knowledge/{ks.Id}/conflicts/{conflictId}/reopen", content: null);
        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);
        var reopened = await reopenResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("open", reopened.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reopened.GetProperty("resolved_at").ValueKind);
    }

    [Fact]
    public async Task GetContext_returns_conflict_plus_empty_evidence_when_no_chunks()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "context");
        var conflictId = SeedConflict(app, ks.Id, status: "open", ctype: "duplicate", signature: "dup|Person|用户");

        var response = await client.GetAsync($"/api/knowledge/{ks.Id}/conflicts/{conflictId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ctx = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(conflictId, ctx.GetProperty("conflict").GetProperty("id").GetGuid());
        Assert.Equal(0, ctx.GetProperty("evidence").GetArrayLength());
    }

    [Fact]
    public async Task ListReconciliations_returns_seed_rows()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "list-recon");
        SeedReconciliation(app, ks.Id, slot: "domain", label: "owns");

        var response = await client.GetAsync($"/api/knowledge/{ks.Id}/reconciliations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var items = list.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("domain", items[0].GetProperty("slot").GetString());
        Assert.Equal("owns", items[0].GetProperty("property_label").GetString());
    }

    [Fact]
    public async Task EditReconciliationReason_updates_reason()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "edit-reason");
        var rid = SeedReconciliation(app, ks.Id, slot: "range", label: "produces");

        var response = await client.PatchAsJsonAsync(
            $"/api/knowledge/{ks.Id}/reconciliations/{rid}",
            new { reason = "Auto-detected; confirm with PO team." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify the persisted row reflects the edit.
        var db = app.CreateDbContext();
        var row = db.TboxReconciliations.Single(r => r.Id == rid);
        Assert.Equal("Auto-detected; confirm with PO team.", row.Reason);
    }

    [Fact]
    public async Task RevokeReconciliation_deletes_row()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var client = await AuthenticatedClientAsync(app);
        var ks = await SeedKnowledgeSystemAsync(app, "revoke-recon");
        var rid = SeedReconciliation(app, ks.Id, slot: "domain", label: "manages");

        var response = await client.DeleteAsync($"/api/knowledge/{ks.Id}/reconciliations/{rid}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = app.CreateDbContext();
        Assert.False(db.TboxReconciliations.Any(r => r.Id == rid));
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
            Name = $"conflict-tests-{tag}",
            Description = "Seed KS for conflict contract tests.",
            GraphIri = $"http://goodcrew.local/ks/{tag}",
            BaseIri = $"http://goodcrew.local/ks/{tag}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static Guid SeedConflict(
        AuthTestWebApplicationFactory app, Guid ksId, string status, string ctype, string? signature = null)
    {
        var db = app.CreateDbContext();
        var id = Guid.NewGuid();
        db.Conflicts.Add(new ConflictEntity
        {
            Id = id,
            LegacyId = TestLegacyIds.Next("conflict"),
            KnowledgeSystemId = ksId,
            Signature = signature ?? $"{ctype}|{Guid.NewGuid():N}",
            Ctype = ctype,
            Severity = "error",
            Status = status,
            Title = $"{ctype} conflict (seed)",
            Detail = "Seeded for test.",
            Payload = System.Text.Json.JsonDocument.Parse("""{"entities":[],"resolutions":[]}"""),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedReconciliation(
        AuthTestWebApplicationFactory app, Guid ksId, string slot, string label)
    {
        var db = app.CreateDbContext();
        var id = Guid.NewGuid();
        db.TboxReconciliations.Add(new TboxReconciliationEntity
        {
            Id = id,
            LegacyId = TestLegacyIds.Next("tbox_reconciliation"),
            KnowledgeSystemId = ksId,
            Slot = slot,
            PropertyLabel = label,
            PropertyIri = $"http://goodcrew.local/{label}",
            Candidates = System.Text.Json.JsonDocument.Parse("""["Cat","Dog"]"""),
            Choice = "common_super",
            ChosenLabel = "Animal",
            ResolvedBy = "agent",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }
}