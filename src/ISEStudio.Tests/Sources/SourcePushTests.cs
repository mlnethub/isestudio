using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Authentication;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Authorization;
using ISEStudio.Sources;
using ISEStudio.Storage;
using ISEStudio.Tests.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.AspNetCore.Authorization;
using ISEStudio.Controllers;

namespace ISEStudio.Tests.Sources;

public sealed class SourcePushTests
{
    [Fact]
    public async Task Task7_oversized_literal_returns_safe_413_without_writes()
    {
        using var app = new PushFactory(false);
        var seed = await Seed(app, "statements");
        using var client = app.CreateClient();
        using var response = await SendStatements(client, seed, seed.Token,
            [new("too-large", "urn:subject", "urn:predicate", new string('x', 8193), "literal")]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain(seed.Token, await response.Content.ReadAsStringAsync());
        using var db = app.CreateDbContext();
        Assert.Empty(await db.WorkspaceStatements.ToListAsync());
        Assert.Empty(await db.SourceStatements.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task7_exact_statement_batch_and_body_limits_are_accepted(bool sso)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app, "statements");
        using var client = app.CreateClient();
        var input = new SourceStatementRequest("boundary", "urn:subject", "urn:predicate", "hello", "literal");
        using var batch = await SendStatements(client, seeded, seeded.Token, Enumerable.Repeat(input, 1000).ToArray());
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Equal(1000, (await batch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").GetArrayLength());
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { statements = new[] { input } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var bytes = new byte[SourcePushService.MaxItemBytes];
        payload.CopyTo(bytes, 0);
        bytes.AsSpan(payload.Length).Fill((byte)' ');
        foreach (var unknownLength in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/knowledge/{seeded.Ks}/ingestion-sources/{seeded.Source}/statements");
            request.Headers.Authorization = new("Bearer", seeded.Token);
            request.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }
        using var db = app.CreateDbContext();
        Assert.Single(await db.SourceStatements.ToListAsync());
        Assert.Single(await db.SourceStatementFacts.ToListAsync());
        Assert.Single(await db.WorkspaceStatements.ToListAsync());
        Assert.Equal(0, app.Blobs.Writes);
    }

    [Theory]
    [InlineData(false, "viewer")]
    [InlineData(false, "editor")]
    [InlineData(false, "admin")]
    [InlineData(true, "viewer")]
    [InlineData(true, "editor")]
    [InlineData(true, "admin")]
    public async Task Task7_user_roles_cannot_push_but_can_read_statement_provenance(bool sso, string role)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app, "statements");
        using var client = app.CreateClient();
        if (role == "admin") await app.SeedAdminAsync();
        else
        {
            await app.SeedUserAsync(role);
            using var db = app.CreateDbContext();
            var actor = await db.Users.SingleAsync(item => item.Username == role);
            db.KSGrants.Add(new KSGrantEntity { KnowledgeSystemId = seeded.Ks, UserId = actor.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await app.AuthenticateAsAsync(client, role == "admin" ? null : role);
        var input = new SourceStatementRequest("readable", "urn:subject", "urn:predicate", "hello", "literal");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, null, [input])).StatusCode);
        using (var db = app.CreateDbContext()) Assert.Empty(await db.SourceStatements.ToListAsync());
        Assert.Equal(HttpStatusCode.OK, (await SendStatements(client, seeded, seeded.Token, [input])).StatusCode);
        var groups = await client.GetFromJsonAsync<JsonElement>($"/api/knowledge/{seeded.Ks}/provenance");
        var group = Assert.Single(groups.EnumerateArray());
        Assert.StartsWith("rdf|", group.GetProperty("axiom_key").GetString());
        var provenance = Assert.Single(group.GetProperty("sources").EnumerateArray());
        Assert.Equal(seeded.Source, provenance.GetProperty("source_id").GetGuid());
        Assert.Equal("readable", provenance.GetProperty("external_statement_id").GetString());
        Assert.Equal("API", provenance.GetProperty("source_name_snapshot").GetString());
        Assert.NotEqual(Guid.Empty, provenance.GetProperty("source_statement_id").GetGuid());
        Assert.DoesNotContain(seeded.Token, groups.GetRawText());
        using var anonymousClient = app.CreateClient();
        using var query = new HttpRequestMessage(HttpMethod.Get, $"/api/knowledge/{seeded.Ks}/provenance");
        query.Headers.Authorization = new("Bearer", seeded.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.SendAsync(query)).StatusCode);
    }

    [Fact]
    public async Task Task7_single_conflict_invalid_terms_and_batch_partial_commit_have_distinct_HTTP_statuses()
    {
        using var app = new PushFactory(false);
        var seeded = await Seed(app, "statements");
        using var client = app.CreateClient();
        var input = new SourceStatementRequest("stable", "urn:subject", "urn:predicate", "hello", "literal");
        Assert.Equal(HttpStatusCode.OK, (await SendStatements(client, seeded, seeded.Token, [input])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendStatements(client, seeded, seeded.Token,
            [input with { Datatype = "http://www.w3.org/2001/XMLSchema#string" }])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendStatements(client, seeded, seeded.Token,
            [input with { Object = "changed" }])).StatusCode);
        foreach (var invalid in new[] {
            input with { Id = "bad-predicate", Predicate = "relative" },
            input with { Id = "bad-iri", ObjectKind = "iri", Object = "urn:bad value" },
            input with { Id = "bad-subject", SubjectKind = "literal" },
            input with { Id = "bad-language", Language = "en", Datatype = "urn:type" },
            input with { Id = "bad-kind", ObjectKind = "unknown" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendStatements(client, seeded, seeded.Token, [invalid])).StatusCode);
        using var partial = await SendStatements(client, seeded, seeded.Token,
            [input with { Id = "second", Object = "second" }, input with { Predicate = "invalid" }]);
        Assert.Equal(HttpStatusCode.MultiStatus, partial.StatusCode);
        var results = (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.Equal(200, results[0].GetProperty("status").GetInt32());
        Assert.Equal(400, results[1].GetProperty("status").GetInt32());
        using var db = app.CreateDbContext();
        Assert.Equal(2, await db.SourceStatements.CountAsync());
        Assert.Equal(2, await db.SourceStatementFacts.CountAsync());
        Assert.Equal(2, await db.WorkspaceStatements.CountAsync());
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Empty(await db.Chunks.ToListAsync());
        Assert.Empty(await db.DocumentFileVersions.ToListAsync());
        Assert.Equal(0, app.Blobs.Writes);
        var audits = await db.AuditEvents.Where(item => item.Action == "source.statements.push").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => {
            Assert.DoesNotContain(seeded.Token, audit.Detail!.RootElement.GetRawText());
            Assert.DoesNotContain("hello", audit.Detail.RootElement.GetRawText());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task7_statement_auth_scope_limits_and_passive_kind_are_enforced(bool sso)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app, "statements");
        using var client = app.CreateClient();
        var input = new SourceStatementRequest("stable", "urn:subject", "urn:predicate", "hello", "literal");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, null, [input])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, "old-token", [input])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded with { Ks = Guid.NewGuid() }, seeded.Token, [input])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded with { Source = Guid.NewGuid() }, seeded.Token, [input])).StatusCode);
        var jwt = app.Issuer.CreateToken(Guid.NewGuid().ToString(), app.Issuer.ClientId, "jwt-admin", realmRoles: ["admin"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, jwt, [input])).StatusCode);
        await app.SeedAdminAsync();
        await app.AuthenticateAsAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, null, [input])).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var minted = await scope.ServiceProvider.GetRequiredService<IKnowledgeApiTokenService>().CreateAsync(
                new KnowledgeApiTokenCreateRequest(seeded.Ks, null, "Read only", ["ontology:read"], null), default);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, minted.Plaintext, [input])).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await SendStatements(client, seeded, seeded.Token, [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendStatements(client, seeded, seeded.Token, Enumerable.Repeat(input, 1001).ToArray())).StatusCode);
        foreach (var unknownLength in new[] { false, true })
        {
            using var oversized = new HttpRequestMessage(HttpMethod.Post, $"/api/knowledge/{seeded.Ks}/ingestion-sources/{seeded.Source}/statements");
            oversized.Headers.Authorization = new("Bearer", seeded.Token);
            var bytes = new byte[SourcePushService.MaxItemBytes + 1];
            oversized.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        }
        var endpoint = $"/api/knowledge/{seeded.Ks}/ingestion-sources";
        var kinds = await client.GetFromJsonAsync<JsonElement>(endpoint + "/kinds");
        Assert.Contains(kinds.EnumerateArray(), item => item.GetProperty("kind").GetString() == "statements" && !item.GetProperty("active_sync").GetBoolean());
        Assert.DoesNotContain(kinds.EnumerateArray(), item => item.GetProperty("kind").GetString() is "azure_blob" or "memory" or "upload");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(endpoint + $"/{seeded.Source}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new { kind = "statements", name = "Scheduled", sync_interval_minutes = 10 })).StatusCode);
        using (var db = app.CreateDbContext())
        {
            Assert.Empty(await db.WorkspaceStatements.ToListAsync());
            Assert.Empty(await db.SourceStatements.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await SendStatements(client, seeded, seeded.Token, [input])).StatusCode);
        using var anonymousClient = app.CreateClient();
        using var query = new HttpRequestMessage(HttpMethod.Get, endpoint);
        query.Headers.Authorization = new("Bearer", seeded.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.SendAsync(query)).StatusCode);
        using (var db = app.CreateDbContext())
        {
            var source = await db.Sources.SingleAsync(item => item.Id == seeded.Source);
            source.Kind = "api";
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendStatements(client, seeded, seeded.Token, [input])).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendStatements(HttpClient client, Seeded seeded, string? token, SourceStatementRequest[] statements)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/knowledge/{seeded.Ks}/ingestion-sources/{seeded.Source}/statements");
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        request.Content = JsonContent.Create(new { statements }, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Statements_source_bearer_appends_without_documents(bool sso)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app, "statements");
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/knowledge/{seeded.Ks}/ingestion-sources/{seeded.Source}/statements");
        request.Headers.Authorization = new("Bearer", seeded.Token);
        request.Content = JsonContent.Create(new { statements = new[] { new {
            id = "upstream-1", subject = "https://example.test/s", predicate = "https://example.test/p",
            @object = "hello", object_kind = "literal", language = "EN" } } });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        using var db = app.CreateDbContext();
        Assert.Single(await db.WorkspaceStatements.Where(item => item.KnowledgeSystemId == seeded.Ks && item.Layer == "ABox").ToListAsync());
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Empty(await db.Chunks.ToListAsync());
        Assert.Empty(await db.DocumentFileVersions.ToListAsync());
        Assert.Equal(0, app.Blobs.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_bearer_works_with_default_auth_and_retries_pin_versions_without_runs(bool sso)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app);
        using var client = app.CreateClient();
        for (var index = 0; index < 3; index++)
        {
            using var request = Request(seeded, seeded.Token, content: index == 2 ? "changed" : "original");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        using var db = app.CreateDbContext();
        var document = await db.Documents.SingleAsync(item => item.SourceId == seeded.Source);
        Assert.Equal("report.txt", document.OriginalFilename);
        Assert.Equal("text/plain", document.Mime);
        Assert.Equal("upstream-id", document.ExternalKey);
        Assert.Equal(new[] { 1, 2 }, await db.DocumentFileVersions.Where(item => item.DocumentId == document.Id)
            .OrderBy(item => item.Version).Select(item => item.Version).ToArrayAsync());
        Assert.Equal(2, await db.DocumentParseJobs.CountAsync(item => item.DocumentId == document.Id));
        Assert.Empty(await db.SourceSyncRuns.ToListAsync());
        Assert.Empty(await db.SourceSyncJobs.ToListAsync());
        Assert.Equal(2, app.Blobs.Writes);
        Assert.Equal("never", (await db.Sources.SingleAsync(item => item.Id == seeded.Source)).LastSyncStatus);
        using var query = new HttpRequestMessage(HttpMethod.Get, $"/api/knowledge/{seeded.Ks}/ingestion-sources");
        query.Headers.Authorization = new("Bearer", seeded.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(query)).StatusCode);
        using var external = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/knowledge-systems/task6-{seeded.Ks:N}/query");
        external.Headers.Authorization = new("Bearer", seeded.Token);
        external.Content = JsonContent.Create(new { query = "SELECT ?s WHERE { ?s ?p ?o } LIMIT 1" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(external)).StatusCode);
    }

    public static IEnumerable<object[]> RejectedRequests()
    {
        foreach (var sso in new[] { false, true })
            foreach (var scenario in new[] { "missing", "old", "cross-ks", "cross-source", "folder", "statements",
                "readonly", "jwt", "cookie-viewer", "cookie-editor", "cookie-admin", "duplicate", "comma", "basic",
                "query-token", "empty-key", "missing-key", "duplicate-key", "filename", "duplicate-filename",
                "missing-mime", "empty-body", "body-token", "config", "no-secret-key", "oversize", "chunked-oversize" })
                yield return [scenario, sso];
    }

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public async Task Invalid_push_does_not_write_blobs_or_database(string scenario, bool sso)
    {
        using var app = new PushFactory(sso, scenario != "no-secret-key");
        var seeded = await Seed(app, scenario is "folder" or "statements" ? scenario : "api");
        using var client = app.CreateClient();
        var token = scenario switch
        {
            "missing" or "query-token" or "body-token" => null,
            "jwt" => app.Issuer.CreateToken(Guid.NewGuid().ToString(), app.Issuer.ClientId, "jwt-admin", realmRoles: ["admin"]),
            _ => seeded.Token,
        };
        if (scenario == "jwt" && sso)
        {
            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            authenticated.Headers.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authenticated)).StatusCode);
        }
        if (scenario == "readonly")
        {
            using var scope = app.Services.CreateScope();
            var minted = await scope.ServiceProvider.GetRequiredService<IKnowledgeApiTokenService>().CreateAsync(
                new KnowledgeApiTokenCreateRequest(seeded.Ks, null, "Read only", ["ontology:read"], null), default);
            token = minted.Plaintext;
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IKnowledgeApiTokenService>().VerifyAsync(token, default));
        }
        if (scenario == "old")
        {
            await app.SeedAdminAsync();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var actor = await db.Users.SingleAsync(item => item.IsAdmin);
            var rotated = await scope.ServiceProvider.GetRequiredService<SourceService>().RotateTokenAsync(seeded.Ks, seeded.Source, actor, default);
            Assert.Equal(200, rotated.StatusCode);
            Assert.NotEqual(token, rotated.Value!.Token);
        }
        if (scenario == "config")
        {
            using var db = app.CreateDbContext();
            (await db.Sources.SingleAsync()).Config = "{\"unsupported\":true}";
            await db.SaveChangesAsync();
        }
        if (scenario.StartsWith("cookie-", StringComparison.Ordinal))
        {
            if (scenario == "cookie-admin")
            {
                await app.SeedAdminAsync();
                await app.AuthenticateAsAsync(client);
            }
            else
            {
                var role = scenario[7..];
                await app.SeedUserAsync(role);
                using var db = app.CreateDbContext();
                var actor = await db.Users.SingleAsync(item => item.Username == role);
                db.KSGrants.Add(new KSGrantEntity { KnowledgeSystemId = seeded.Ks, UserId = actor.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                await app.AuthenticateAsAsync(client, role);
            }
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/knowledge/{seeded.Ks}/ingestion-sources")).StatusCode);
            token = null;
        }
        Guid otherSource = seeded.Source;
        if (scenario == "cross-source")
        {
            using var db = app.CreateDbContext();
            otherSource = Guid.NewGuid();
            db.Sources.Add(new SourceEntity { Id = otherSource, KnowledgeSystemId = seeded.Ks, Kind = "api", Name = "Other",
                CreatedAt = DateTimeOffset.UtcNow, IngestTokenCiphertext = app.Services.GetRequiredService<ISourceSecretProtector>().Seal(
                    "other-source-token", $"{seeded.Ks:D}:{otherSource:D}:token") });
            await db.SaveChangesAsync();
        }
        var target = seeded with
        {
            Ks = scenario == "cross-ks" ? Guid.NewGuid() : seeded.Ks,
            Source = otherSource,
        };
        using var request = Request(target, token, key: scenario == "empty-key" ? " " : "upstream-id");
        if (scenario == "duplicate") request.Headers.TryAddWithoutValidation("Authorization", "Bearer other");
        if (scenario == "comma") request.Headers.TryAddWithoutValidation("Authorization", "Bearer other,Bearer " + seeded.Token);
        if (scenario == "basic") request.Headers.Authorization = new("Basic", seeded.Token);
        if (scenario == "query-token") request.RequestUri = new(request.RequestUri + "&token=" + seeded.Token, UriKind.Relative);
        if (scenario == "duplicate-key") request.RequestUri = new(request.RequestUri + "&external_key=other", UriKind.Relative);
        if (scenario == "filename") request.Headers.Remove("X-Source-Filename");
        if (scenario == "duplicate-filename") request.Headers.TryAddWithoutValidation("X-Source-Filename", "other.txt");
        if (scenario == "missing-key") request.RequestUri = new(request.RequestUri!.OriginalString.Split('?')[0], UriKind.Relative);
        if (scenario == "missing-mime") request.Content!.Headers.ContentType = null;
        if (scenario == "empty-body") request.Content = new ByteArrayContent([]) { Headers = { ContentType = new("text/plain") } };
        if (scenario == "body-token") request.Content = new StringContent(seeded.Token, Encoding.UTF8, "text/plain");
        if (scenario is "oversize" or "chunked-oversize")
        {
            request.Content = scenario == "oversize"
                ? new ByteArrayContent(new byte[20 * 1024 * 1024 + 1])
                : new UnknownLengthContent(new byte[20 * 1024 * 1024 + 1]);
            request.Content.Headers.ContentType = new("text/plain");
        }
        using var response = await client.SendAsync(request);
        Assert.Equal(scenario is "oversize" or "chunked-oversize" ? HttpStatusCode.RequestEntityTooLarge
            : scenario is "empty-key" or "missing-key" or "duplicate-key" or "filename" or "duplicate-filename" or "missing-mime" or "empty-body" ? HttpStatusCode.BadRequest
            : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.Blobs.Writes);
        using var stateDb = app.CreateDbContext();
        Assert.Empty(await stateDb.Documents.ToListAsync());
        Assert.Empty(await stateDb.SourceDocumentBindings.ToListAsync());
        Assert.Empty(await stateDb.DocumentFileVersions.ToListAsync());
        Assert.Empty(await stateDb.DocumentParseJobs.ToListAsync());
        Assert.DoesNotContain(seeded.Token, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exactly_twenty_MiB_is_accepted_even_without_content_length(bool sso)
    {
        using var app = new PushFactory(sso);
        var seeded = await Seed(app);
        using var client = app.CreateClient();
        using var request = Request(seeded, seeded.Token);
        request.Content = new UnknownLengthContent(new byte[20 * 1024 * 1024]);
        request.Content.Headers.ContentType = new("application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(1, app.Blobs.Writes);
        using var db = app.CreateDbContext();
        Assert.Equal(20 * 1024 * 1024, (await db.Documents.SingleAsync()).SizeBytes);
    }

    [Fact]
    public async Task Rate_limit_is_scoped_no_cache_and_never_writes_rejected_items()
    {
        using var app = new PushFactory(false);
        var first = await Seed(app);
        var second = await Seed(app);
        using var client = app.CreateClient();
        for (var index = 0; index < 60; index++)
        {
            using var request = Request(first, first.Token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = Request(first, first.Token, content: "must-not-write");
        using var rejection = await client.SendAsync(limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejection.StatusCode);
        Assert.Contains("no-store", rejection.Headers.CacheControl!.ToString());
        Assert.Equal(1, app.Blobs.Writes);
        using var alternateCasing = Request(first, first.Token, content: "must-not-write");
        alternateCasing.RequestUri = new(alternateCasing.RequestUri!.OriginalString
            .Replace(first.Ks.ToString(), first.Ks.ToString().ToUpperInvariant(), StringComparison.Ordinal)
            .Replace(first.Source.ToString(), first.Source.ToString().ToUpperInvariant(), StringComparison.Ordinal), UriKind.Relative);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(alternateCasing)).StatusCode);
        using var independent = Request(second, second.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(independent)).StatusCode);
    }

    [Fact]
    public async Task Public_coordinator_cannot_bypass_push_token_guard_and_only_action_is_anonymous()
    {
        using var app = new PushFactory(false);
        var seeded = await Seed(app);
        using var scope = app.Services.CreateScope();
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("bypass"));
        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<SourceSyncCoordinator>()
            .IngestItemAsync(seeded.Source, new SourceItem("key", "report.txt", "text/plain", body), default));
        Assert.Equal(0, app.Blobs.Writes);
        Assert.Empty(typeof(SourcePushController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        Assert.Single(typeof(SourcePushController).GetMethod(nameof(SourcePushController.DocumentsAsync))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public async Task Api_is_creatable_passive_and_dtos_do_not_reveal_tokens()
    {
        using var app = new PushFactory(false);
        await app.SeedAdminAsync();
        using var client = app.CreateClient();
        await app.AuthenticateAsAsync(client);
        var seeded = await Seed(app);
        var endpoint = $"/api/knowledge/{seeded.Ks}/ingestion-sources";
        using var response = await client.PostAsJsonAsync(endpoint, new { kind = "api", name = "New API" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var kinds = await client.GetFromJsonAsync<JsonElement>(endpoint + "/kinds");
        Assert.Contains(kinds.EnumerateArray(), item => item.GetProperty("kind").GetString() == "api" && !item.GetProperty("active_sync").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(endpoint + $"/{seeded.Source}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(endpoint, new { kind = "api", name = "Scheduled", sync_interval_minutes = 10 })).StatusCode);
        foreach (var path in new[] { endpoint, endpoint + $"/{seeded.Source}" })
            Assert.DoesNotContain(seeded.Token, await client.GetStringAsync(path));
    }

    private static HttpRequestMessage Request(Seeded seeded, string? token, string key = "upstream-id", string content = "original")
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/knowledge/{seeded.Ks}/ingestion-sources/{seeded.Source}/documents?external_key={Uri.EscapeDataString(key)}");
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-Source-Filename", "report.txt");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return request;
    }

    private static async Task<Seeded> Seed(PushFactory app, string kind = "api")
    {
        using var db = app.CreateDbContext();
        var ks = new KnowledgeSystemEntity { Id = Guid.NewGuid(), Name = "push-test", CreatedAt = DateTimeOffset.UtcNow };
        ks.PublicId = $"task6-{ks.Id:N}";
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = ks.Id, Kind = kind, Name = "API", CreatedAt = DateTimeOffset.UtcNow };
        var token = "test-source-token-" + Guid.NewGuid().ToString("N");
        if (app.HasKey)
            source.IngestTokenCiphertext = app.Services.GetRequiredService<ISourceSecretProtector>().Seal(token, $"{ks.Id:D}:{source.Id:D}:token");
        db.KnowledgeSystems.Add(ks);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return new(ks.Id, source.Id, token);
    }

    private sealed record Seeded(Guid Ks, Guid Source, string Token);

    private sealed class PushFactory : AuthTestWebApplicationFactory
    {
        private readonly string? _oldAuthority;
        private readonly string? _oldClientId;
        private readonly bool _sso;
        public TestJwtIssuer Issuer { get; } = new();
        public bool HasKey { get; }
        public CountingBlobs Blobs { get; } = new();
        public PushFactory(bool sso, bool hasKey = true) : base(null, hasKey ? Convert.ToBase64String(new byte[32]) : null)
        {
            HasKey = hasKey;
            _sso = sso;
            _oldAuthority = Environment.GetEnvironmentVariable("ISEStudio__Auth__Keycloak__Authority");
            _oldClientId = Environment.GetEnvironmentVariable("ISEStudio__Auth__Keycloak__ClientId");
            Environment.SetEnvironmentVariable("ISEStudio__Auth__Keycloak__Authority", sso ? Issuer.Authority : "");
            Environment.SetEnvironmentVariable("ISEStudio__Auth__Keycloak__ClientId", Issuer.ClientId);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(Blobs);
                if (_sso) services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer.Authority };
                    foreach (var key in new JsonWebKeySet(Issuer.JwksJson()).GetSigningKeys()) configuration.SigningKeys.Add(key);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("ISEStudio__Auth__Keycloak__Authority", _oldAuthority);
                Environment.SetEnvironmentVariable("ISEStudio__Auth__Keycloak__ClientId", _oldClientId);
            }
        }
    }

    private sealed class CountingBlobs : IBlobStore
    {
        private readonly LocalCasBlobStore _inner = new(Path.Combine(Path.GetTempPath(), "task6-blobs-" + Guid.NewGuid().ToString("N")));
        public int Writes { get; private set; }
        public Task<BlobWriteResult> PutAsync(Stream content, CancellationToken ct) { Writes++; return _inner.PutAsync(content, ct); }
        public Task<Stream?> GetAsync(string sha256, CancellationToken ct) => _inner.GetAsync(sha256, ct);
        public Task<bool> ExistsAsync(string sha256, CancellationToken ct) => _inner.ExistsAsync(sha256, ct);
        public Task<bool> RemoveAsync(string sha256, CancellationToken ct) => _inner.RemoveAsync(sha256, ct);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}