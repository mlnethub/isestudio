using System.Net;
using System.Text;
using System.Text.Json;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Sources;
using ISEStudio.Sources.Networking;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ISEStudio.IntegrationTests.Ingestion;

[Collection(SourceSyncTestCollection.Name)]
public sealed class SourceContentConnectorTests(PostgresGraphFixture fixture) : IClassFixture<PostgresGraphFixture>
{
    [Theory]
    [InlineData("webdav")]
    [InlineData("notion")]
    public async Task Full_content_path_keeps_identity_versions_blobs_parse_jobs_and_complete_deletion_reconciliation(string kind)
    {
        await using var services = Services();
        var harness = await Create(services, kind);
        harness.Http.Mode = "initial";
        var first = await harness.Run();
        Assert.True(first.IsComplete);
        Assert.Equal(2, first.Added);
        Assert.Empty(first.Errors);
        var baseline = await harness.State();
        Assert.Equal(2, baseline.Documents.Count);
        Assert.Equal(2, baseline.Versions.Count);
        Assert.Equal(2, baseline.Jobs.Count);
        Assert.All(baseline.Versions, version => Assert.Equal(harness.Http.Time, version.DocTime));
        Assert.All(baseline.Jobs, job => Assert.Equal(baseline.Versions.Single(version => version.Id == job.DocumentFileVersionId).Sha256, job.Sha256));
        var original = baseline.Bindings.Single(binding => binding.ExternalKey == harness.Http.FirstKey);
        Assert.Equal(0, (await harness.Run()).Updated);
        Assert.Equal(2, (await harness.State()).Versions.Count);
        harness.Http.Mode = "changed";
        Assert.Equal(1, (await harness.Run()).Updated);
        var changed = await harness.State();
        Assert.Equal(original.DocumentId, changed.Bindings.Single(binding => binding.ExternalKey == original.ExternalKey).DocumentId);
        Assert.Equal(new[] { 1, 2 }, changed.Versions.Where(version => version.DocumentId == original.DocumentId).OrderBy(version => version.Version).Select(version => version.Version));
        Assert.Equal(3, changed.Jobs.Count);
        foreach (var version in changed.Versions)
        {
            await using var blob = await services.GetRequiredService<IBlobStore>().GetAsync(version.Sha256, default);
            Assert.NotNull(blob);
            using var reader = new StreamReader(blob);
            Assert.Contains(harness.Http.Marker, await reader.ReadToEndAsync());
        }
        harness.Http.Mode = "deleted";
        var complete = await harness.Run();
        Assert.True(complete.IsComplete);
        Assert.Empty(complete.Errors);
        var reconciled = await harness.State();
        Assert.Null(reconciled.Bindings.Single(binding => binding.ExternalKey == harness.Http.FirstKey).MissingSince);
        Assert.NotNull(reconciled.Bindings.Single(binding => binding.ExternalKey == harness.Http.SecondKey).MissingSince);
        Assert.Equal(2, reconciled.Documents.Count);
        Assert.Equal(3, reconciled.Versions.Count);
        Assert.Equal(3, reconciled.Jobs.Count);
    }

    [Theory]
    [InlineData("webdav", "partial")]
    [InlineData("webdav", "pages")]
    [InlineData("webdav", "depth")]
    [InlineData("webdav", "root-get")]
    [InlineData("webdav", "root-propfind")]
    [InlineData("webdav", "partial206")]
    [InlineData("webdav", "content-range")]
    [InlineData("notion", "partial")]
    [InlineData("notion", "pages")]
    [InlineData("notion", "blocks")]
    public async Task Partial_content_scan_commits_successes_retains_old_versions_and_never_reconciles(string kind, string fault)
    {
        await using var services = Services();
        var harness = await Create(services, kind);
        await harness.Run();
        var original = await harness.State();
        harness.Http.Mode = fault;
        await using (var db = await services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContextAsync())
        {
            var source = await db.Sources.SingleAsync(item => item.Id == harness.Id);
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.Config)!;
            if (fault == "pages") values["max_pages"] = JsonSerializer.SerializeToElement(kind == "notion" ? 2 : 1);
            if (fault == "depth") values["max_depth"] = JsonSerializer.SerializeToElement(0);
            source.Config = JsonSerializer.Serialize(values);
            await db.SaveChangesAsync();
        }
        var failed = await harness.Run();
        Assert.False(failed.IsComplete);
        Assert.NotEmpty(failed.Errors);
        Assert.Equal(1, failed.Updated);
        Assert.DoesNotContain("private-", string.Join(" ", failed.Errors));
        var retained = await harness.State();
        Assert.Equal(2, retained.Documents.Count);
        Assert.Equal(3, retained.Versions.Count);
        Assert.Equal(3, retained.Jobs.Count);
        Assert.All(retained.Bindings, binding => Assert.Null(binding.MissingSince));
        Assert.All(retained.Documents, document => Assert.Null(document.MissingSince));
        Assert.Equal("failed", retained.Source.LastSyncStatus);
        Assert.Equal("failed", retained.Run.Status);
        Assert.DoesNotContain(harness.Http.Requests, uri => uri.AbsolutePath.StartsWith("/private/", StringComparison.Ordinal));
        Assert.All(original.Versions, version => Assert.Contains(retained.Versions, current => current.Id == version.Id));
        Assert.All(original.Jobs, job => Assert.Contains(retained.Jobs, current => current.Id == job.Id));
    }

    [Theory]
    [InlineData("page")]
    [InlineData("database")]
    public async Task Notion_scopes_keep_immutable_identity_and_reconcile_only_complete_snapshots(string scope)
    {
        await using var services = Services();
        var harness = await Create(services, "notion", scope);
        var first = await harness.Run();
        Assert.True(first.IsComplete);
        Assert.Empty(first.Errors);
        Assert.Equal(scope == "page" ? 1 : 2, first.Added);
        var baseline = await harness.State();
        var binding = baseline.Bindings.Single(item => item.ExternalKey == harness.Http.FirstKey);
        harness.Http.Mode = "changed";
        var changed = await harness.Run();
        Assert.True(changed.IsComplete);
        Assert.Equal(1, changed.Updated);
        var updated = await harness.State();
        Assert.Equal(binding.DocumentId, updated.Bindings.Single(item => item.ExternalKey == harness.Http.FirstKey).DocumentId);
        Assert.Equal(new[] { 1, 2 }, updated.Versions.Where(item => item.DocumentId == binding.DocumentId).OrderBy(item => item.Version).Select(item => item.Version));
        harness.Http.Mode = "deleted";
        var complete = await harness.Run();
        Assert.True(complete.IsComplete);
        Assert.Empty(complete.Errors);
        var reconciled = await harness.State();
        Assert.Equal(updated.Versions.Count, reconciled.Versions.Count);
        Assert.Equal(updated.Jobs.Count, reconciled.Jobs.Count);
        if (scope == "page") Assert.NotNull(Assert.Single(reconciled.Bindings).MissingSince);
        else
        {
            Assert.Null(reconciled.Bindings.Single(item => item.ExternalKey == harness.Http.FirstKey).MissingSince);
            Assert.NotNull(reconciled.Bindings.Single(item => item.ExternalKey == harness.Http.SecondKey).MissingSince);
        }
    }

    private ServiceProvider Services() => fixture.BuildServices(services =>
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());
        services.AddSingleton<KnowledgeSystemAccessService>();
        services.AddSingleton<IOptions<SourceNetworkOptions>>(Options.Create(new SourceNetworkOptions()));
        services.AddSingleton<ContentTransport>();
        services.AddSingleton<ISafeSourceHttpClient>(provider => new SafeSourceHttpClient(
            new SourceNetworkPolicy(new PublicDns(), provider.GetRequiredService<IOptions<SourceNetworkOptions>>()),
            provider.GetRequiredService<ContentTransport>(), provider.GetRequiredService<IOptions<SourceNetworkOptions>>(), TimeProvider.System));
        services.AddSourceServices();
    });

    private async Task<Harness> Create(ServiceProvider services, string kind, string notionScope = "search")
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = fixture.KnowledgeSystemId, Kind = kind, Name = $"task5-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
        var config = new Dictionary<string, string>();
        if (kind == "webdav") { config["base_url"] = "https://example.test:8443"; config["path"] = "/docs"; config["username"] = "private-user"; config["password"] = "private-password"; }
        else
        {
            config["token"] = "private-token";
            config["scope"] = notionScope;
            if (notionScope != "search") config[notionScope == "page" ? "page_id" : "database_id"] = "11111111-1111-1111-1111-111111111111";
        }
        foreach (var field in new[] { "username", "password", "token" }.Where(config.ContainsKey))
            config[field] = scope.ServiceProvider.GetRequiredService<ISourceSecretProtector>().Seal(config[field], $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field}");
        source.Config = JsonSerializer.Serialize(config);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        var http = services.GetRequiredService<ContentTransport>();
        http.Kind = kind;
        http.Scope = notionScope;
        return new Harness(services, source.Id, http);
    }

    private sealed class Harness(ServiceProvider services, Guid id, ContentTransport http)
    {
        private Guid _runId;
        public Guid Id => id;
        public ContentTransport Http => http;
        public async Task<SourceSyncResult> Run()
        {
            await using var scope = services.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<SourceSyncJobStore>();
            var job = await jobs.EnqueueAsync(id, default);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(default));
            Assert.Equal(id, claim.SourceId);
            _runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await scope.ServiceProvider.GetRequiredService<SourceSyncCoordinator>().RunAsync(id, job, _runId, default);
            await jobs.CompleteAsync(job, _runId, result, default);
            return result;
        }
        public async Task<State> State()
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContextAsync();
            var bindings = await db.SourceDocumentBindings.AsNoTracking().Where(item => item.SourceId == id).ToListAsync();
            var ids = bindings.Select(binding => binding.DocumentId).ToArray();
            return new(await db.Sources.AsNoTracking().SingleAsync(item => item.Id == id), await db.SourceSyncRuns.AsNoTracking().SingleAsync(item => item.Id == _runId), bindings,
                await db.Documents.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(), await db.DocumentFileVersions.AsNoTracking().Where(item => ids.Contains(item.DocumentId)).ToListAsync(),
                await db.DocumentParseJobs.AsNoTracking().Where(item => ids.Contains(item.DocumentId)).ToListAsync());
        }
    }
    private sealed record State(SourceEntity Source, SourceSyncRunEntity Run, List<SourceDocumentBindingEntity> Bindings, List<DocumentEntity> Documents, List<DocumentFileVersionEntity> Versions, List<DocumentParseJobEntity> Jobs);
    private sealed class PublicDns : ISourceDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
    }
    private sealed class ContentTransport : ISourceHttpTransport
    {
        private const string FirstId = "11111111-1111-1111-1111-111111111111";
        private const string SecondId = "22222222-2222-2222-2222-222222222222";
        public string Marker { get; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = "";
        public string Scope { get; set; } = "search";
        public List<Uri> Requests { get; } = [];
        public string Mode { get; set; } = "initial";
        public DateTimeOffset Time { get; } = DateTimeOffset.Parse("2026-09-02T15:04:05Z");
        public string FirstKey => Kind == "webdav" ? "https://example.test:8443/docs/a.txt" : FirstId;
        public string SecondKey => Kind == "webdav" ? "https://example.test:8443/docs/b.txt" : SecondId;
        public async Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
        {
            Assert.Equal(new[] { IPAddress.Parse("8.8.8.8") }, addresses);
            Assert.NotNull(request.Headers.Authorization);
            Assert.Empty(request.Headers.IfNoneMatch);
            Requests.Add(request.RequestUri!);
            var mime = "application/json";
            var status = HttpStatusCode.OK;
            string? location = null;
            string body;
            if (Kind == "webdav")
            {
                if (request.Method.Method == "PROPFIND")
                {
                    Assert.Equal("1", Assert.Single(request.Headers.GetValues("Depth")));
                    mime = "application/xml";
                    status = HttpStatusCode.MultiStatus;
                    if (request.RequestUri!.AbsolutePath == "/docs/sub/")
                    {
                        body = "private-failed-page";
                        status = Mode == "root-propfind" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.BadGateway;
                        if (Mode == "root-propfind") location = "/private/";
                    }
                    else body = "<multistatus xmlns='DAV:'>" + Dav("/docs/", true) + Dav("/docs/a.txt") + (Mode == "deleted" ? "" : Mode is "partial" or "pages" or "depth" or "root-propfind" ? Dav("/docs/sub/", true) : Dav("/docs/b.txt")) + "</multistatus>";
                }
                else
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    mime = "text/plain";
                    var first = request.RequestUri!.AbsolutePath.EndsWith("a.txt", StringComparison.Ordinal);
                    body = Marker + ":" + (first ? Mode == "initial" ? "alpha" : "changed" : "beta");
                    if (!first && Mode == "root-get") { status = HttpStatusCode.TemporaryRedirect; location = "/private/b.txt"; }
                    if (!first && Mode == "partial206") status = HttpStatusCode.PartialContent;
                }
            }
            else
            {
                Assert.Equal("2022-06-28", Assert.Single(request.Headers.GetValues("Notion-Version")));
                if (Scope == "page" && request.RequestUri!.AbsolutePath == "/v1/pages/" + FirstId)
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    body = JsonSerializer.Serialize(Page(FirstId, Mode == "deleted"));
                }
                else if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal(Scope == "database" ? "/v1/databases/" + FirstId + "/query" : "/v1/search", request.RequestUri!.AbsolutePath);
                    using var posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    if (posted.RootElement.TryGetProperty("start_cursor", out _)) { body = "private-rate-limit"; status = HttpStatusCode.TooManyRequests; }
                    else body = JsonSerializer.Serialize(new { @object = "list", results = Mode is "partial" or "pages" ? new[] { Page(FirstId, false) } : new[] { Page(FirstId, false), Page(SecondId, Mode == "deleted") }, has_more = Mode is "partial" or "pages", next_cursor = Mode is "partial" or "pages" ? "next" : null });
                }
                else
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    var first = request.RequestUri!.AbsolutePath.Contains(FirstId, StringComparison.Ordinal);
                    body = JsonSerializer.Serialize(new { @object = "list", results = new[] { new { @object = "block", id = first ? "33333333-3333-3333-3333-333333333333" : "44444444-4444-4444-4444-444444444444", type = "paragraph", has_children = false, paragraph = new { rich_text = new[] { new { plain_text = Marker + ":" + (first ? Mode == "initial" ? "alpha" : "changed" : "beta") } } } } }, has_more = false, next_cursor = (string?)null });
                    if (!first && Mode == "blocks") { body = "private-block-error"; status = HttpStatusCode.Forbidden; }
                }
            }
            var message = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mime) };
            if (location is not null) message.Headers.Location = new Uri(location, UriKind.Relative);
            if (Mode == "content-range" && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/docs/b.txt")
                message.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-3/100");
            return new SourceHttpResponse(message);
        }
        private string Dav(string href, bool collection = false) => $"<response><href>{href}</href><propstat><prop><resourcetype>{(collection ? "<collection/>" : "")}</resourcetype><getlastmodified>{Time:R}</getlastmodified></prop><status>HTTP/1.1 200 OK</status></propstat></response>";
        private object Page(string id, bool archived) => new { @object = "page", id, archived, last_edited_time = Time.ToString("O"), properties = new { Name = new { type = "title", title = new[] { new { plain_text = id == FirstId ? Mode == "initial" ? "Title" : "Renamed" : "Other" } } } } };
    }
}