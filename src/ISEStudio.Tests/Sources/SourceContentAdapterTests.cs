using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Sources.Adapters;
using ISEStudio.Sources.Networking;
using ISEStudio.Tests.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Sources;

public sealed class SourceContentAdapterTests
{
    internal const string PageId = "11111111-1111-1111-1111-111111111111";
    internal const string BlockId = "22222222-2222-2222-2222-222222222222";
    internal const string Search = "https://api.notion.com/v1/search";
    internal static string Children(string id) => $"https://api.notion.com/v1/blocks/{id}/children?page_size=100";

    [Fact]
    public async Task Webdav_breadth_first_discovers_files_not_collections_with_origin_identity_and_time()
    {
        using var harness = new Harness();
        harness.Http.Add("PROPFIND", "https://example.test:8443/docs/", Dav(Entry("/docs/", true), Entry("/docs/one.txt"), Entry("/docs/sub/", true)), "application/xml");
        harness.Http.Add("GET", "https://example.test:8443/docs/one.txt", "first", "text/plain");
        harness.Http.Add("PROPFIND", "https://example.test:8443/docs/sub/", Dav(Entry("/docs/sub/", true), Entry("/docs/sub/two.txt")), "application/xml");
        harness.Http.Add("GET", "https://example.test:8443/docs/sub/two.txt", "second", "text/plain");
        var scan = await harness.Discover("webdav", new { base_url = "https://example.test:8443", path = "/docs/", username = "user", password = "private-password" });
        var items = await SourceAdapterTests.Read(scan);
        Assert.True(scan.IsComplete);
        Assert.Equal(new[] { "https://example.test:8443/docs/one.txt", "https://example.test:8443/docs/sub/two.txt" }, items.Select(item => item.Key));
        Assert.All(items, item => Assert.Equal(DateTimeOffset.Parse("2026-09-02T15:04:05Z"), item.Time));
        Assert.All(harness.Http.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:private-password")), request.Authorization));
        Assert.Equal(new[] { "PROPFIND", "GET", "PROPFIND", "GET" }, harness.Http.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task Notion_search_and_recursive_paginated_blocks_use_sealed_token_fixed_version_and_page_identity()
    {
        using var harness = new Harness();
        harness.Http.Add("POST", Search, List(Page(PageId, "Title")));
        harness.Http.Add("GET", Children(PageId), List(Block(BlockId, "parent", true), more: true, cursor: "next"));
        harness.Http.Add("GET", Children(BlockId), List(Block("33333333-3333-3333-3333-333333333333", "nested")));
        harness.Http.Add("GET", Children(PageId) + "&start_cursor=next", List(Block("44444444-4444-4444-4444-444444444444", "last")));
        var scan = await harness.Discover("notion", new { token = "private-token", scope = "search", query = "Title" });
        var item = Assert.Single(await SourceAdapterTests.Read(scan));
        Assert.True(scan.IsComplete);
        Assert.Equal(PageId, item.Key);
        Assert.Contains("parent\nnested\nlast", item.Body);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:00Z"), item.Time);
        Assert.All(harness.Http.Requests, request =>
        {
            Assert.Equal("Bearer private-token", request.Authorization);
            Assert.Equal("2022-06-28", request.Version);
            Assert.Equal("api.notion.com", request.Uri.Host);
        });
        using var posted = JsonDocument.Parse(harness.Http.Requests[0].Body!);
        Assert.Equal("Title", posted.RootElement.GetProperty("query").GetString());
        Assert.Equal("page", posted.RootElement.GetProperty("filter").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Notion_search_consumes_all_cursors_before_declaring_complete()
    {
        using var harness = new Harness();
        harness.Http.Add("POST", Search, List(Page(PageId, "First"), more: true, cursor: "next"));
        harness.Http.Add("GET", Children(PageId), List(Block(BlockId, "first body")));
        harness.Http.Add("POST", Search, List(Page("33333333-3333-3333-3333-333333333333", "Second")));
        harness.Http.Add("GET", Children("33333333-3333-3333-3333-333333333333"), List(Block("44444444-4444-4444-4444-444444444444", "second body")));
        var scan = await harness.Discover("notion", new { token = "private-token" });
        Assert.Equal(2, (await SourceAdapterTests.Read(scan)).Count);
        Assert.True(scan.IsComplete);
        using var posted = JsonDocument.Parse(harness.Http.Requests.Single(request => request.Method == "POST" && request.Body!.Contains("start_cursor")).Body!);
        Assert.Equal("next", posted.RootElement.GetProperty("start_cursor").GetString());
    }

    [Theory]
    [InlineData("missing-cursor")]
    [InlineData("repeated-cursor")]
    [InlineData("ancestor-cycle")]
    public async Task Notion_invalid_block_pagination_or_cycle_never_emits_a_title_only_page(string fault)
    {
        using var harness = new Harness();
        harness.Http.Add("POST", Search, List(Page(PageId, "Title")));
        harness.Http.Add("GET", Children(PageId), List(Block(fault == "ancestor-cycle" ? PageId : BlockId, "body"), more: fault != "ancestor-cycle", cursor: fault == "repeated-cursor" ? "next" : null));
        harness.Http.Add("GET", Children(PageId) + "&start_cursor=next", JsonSerializer.Serialize(new { @object = "list", results = Array.Empty<object>(), has_more = true, next_cursor = "next" }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("notion", new { token = "private-token" }));
    }

    [Fact]
    public async Task Notion_block_responses_share_the_scan_body_budget()
    {
        using var harness = new Harness(bytes: 2000);
        harness.Http.Add("POST", Search, List(Page(PageId, "Title")));
        harness.Http.Add("GET", Children(PageId), List(Block(BlockId, new string('x', 2100))));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("notion", new { token = "private-token" }));
    }

    [Fact]
    public async Task Webdav_empty_multistatus_cannot_declare_a_complete_directory_snapshot()
    {
        using var harness = new Harness();
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(), "application/xml");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
    }

    [Fact]
    public async Task Notion_archived_pages_still_consume_the_discovery_item_budget()
    {
        using var harness = new Harness(items: 1);
        harness.Http.Add("POST", Search, JsonSerializer.Serialize(new { @object = "list", results = new[] { Page(PageId, "Gone", true), Page(BlockId, "Gone", true) }, has_more = false, next_cursor = (string?)null }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("notion", new { token = "private-token" }));
    }

    [Fact]
    public async Task Webdav_declared_download_length_must_match_content_not_accept_truncation()
    {
        using var harness = new Harness();
        var entry = Entry("/docs/a.txt").Replace("</d:prop>", "<d:getcontentlength>100</d:getcontentlength></d:prop>");
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), entry), "application/xml");
        harness.Http.Add("GET", "https://example.test/docs/a.txt", "truncated", "text/plain");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
    }

    [Theory]
    [InlineData("https://other.test/docs/a.txt")]
    [InlineData("http://example.test/docs/a.txt")]
    [InlineData("https://example.test:8443/docs/a.txt")]
    [InlineData("https://@example.test/docs/a.txt")]
    [InlineData("/elsewhere/a.txt")]
    [InlineData("/docs-other/a.txt")]
    [InlineData("/docs/../a.txt")]
    [InlineData("/docs/%2e%2e/a.txt")]
    [InlineData("/docs/a%2fb.txt")]
    [InlineData("/docs/%252e%252e/a.txt")]
    [InlineData("/docs/a.txt?token=private")]
    [InlineData("/docs/a.txt#private")]
    [InlineData("/docs\\a.txt")]
    [InlineData("/docs/sub/unlisted.txt")]
    public async Task Webdav_rejects_noncanonical_or_out_of_root_hrefs_before_download(string href)
    {
        using var harness = new Harness();
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry(href)), "application/xml");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("failed-status")]
    [InlineData("fake-collection")]
    [InlineData("dtd")]
    [InlineData("duplicate-self")]
    public async Task Webdav_rejects_untrusted_xml_status_or_collection_shapes(string fault)
    {
        using var harness = new Harness();
        var entry = Entry("/docs/a.txt");
        var xml = fault switch
        {
            "namespace" => Dav(entry).Replace("DAV:", "urn:fake"),
            "failed-status" => Dav(entry.Replace("200 OK", "403 Forbidden")),
            "fake-collection" => Dav(entry.Replace("<d:resourcetype></d:resourcetype>", "<d:resourcetype><collection xmlns='urn:fake'/></d:resourcetype>")),
            "duplicate-self" => Dav(Entry("/docs/", true), Entry("/docs/", true)),
            _ => "<!DOCTYPE x [<!ENTITY injected SYSTEM 'file:///private'>]>" + Dav(entry).Replace("/docs/a.txt", "&injected;"),
        };
        harness.Http.Add("PROPFIND", "https://example.test/docs/", xml, "application/xml");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Webdav_only_successful_DAV_properties_supply_time_and_collection_type()
    {
        using var harness = new Harness();
        var file = Entry("/docs/A&amp;B.txt").Replace("A&amp;amp;B", "A&amp;B").Replace("</d:response>", "<d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getlastmodified>not a date</d:getlastmodified></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>");
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), file), "application/xml");
        harness.Http.Add("GET", "https://example.test/docs/A&B.txt", "body", "text/plain");
        var item = Assert.Single(await SourceAdapterTests.Read(await harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" })));
        Assert.Equal("https://example.test/docs/A&B.txt", item.Key);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T15:04:05Z"), item.Time);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("depth")]
    [InlineData("pages")]
    [InlineData("items")]
    [InlineData("download")]
    [InlineData("bytes")]
    public async Task Webdav_partial_failure_keeps_prior_success_and_never_silently_truncates(string fault)
    {
        using var harness = new Harness(items: fault == "items" ? 1 : 1000, bytes: fault == "bytes" ? 3000 : 20 * 1024 * 1024);
        var child = fault == "download" || fault == "bytes" || fault == "items" ? Entry("/docs/b.txt") : Entry("/docs/sub/", true);
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry("/docs/a.txt"), child), "application/xml");
        harness.Http.Add("GET", "https://example.test/docs/a.txt", "successful", "text/plain");
        harness.Http.Add("GET", "https://example.test/docs/b.txt", fault == "bytes" ? new string('x', 4000) : "private-error", "text/plain", fault == "download" ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        harness.Http.Add("PROPFIND", "https://example.test/docs/sub/", Dav(Entry("/docs/sub/", true), Entry("/docs/", true)), "application/xml");
        var scan = await harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs", max_depth = fault == "depth" ? 0 : 8, max_pages = fault == "pages" ? 1 : 100 });
        var items = new List<string>();
        var error = await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items) { await using var content = item.Content; items.Add(item.ExternalKey); }
        });
        Assert.Equal(new[] { "https://example.test/docs/a.txt" }, items);
        Assert.DoesNotContain("private-error", error.ToString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("database")]
    public async Task Notion_specific_scopes_use_official_fixed_endpoints_and_title_is_not_identity(string scope)
    {
        using var harness = new Harness();
        var page = Page(PageId, "Renamed");
        harness.Http.Add(scope == "page" ? "GET" : "POST", scope == "page" ? "https://api.notion.com/v1/pages/" + PageId : "https://api.notion.com/v1/databases/" + BlockId + "/query", scope == "page" ? JsonSerializer.Serialize(page) : List(page));
        harness.Http.Add("GET", Children(PageId), List(Block(BlockId, "body")));
        var config = new Dictionary<string, object> { ["token"] = "private-token", ["scope"] = scope, [scope == "page" ? "page_id" : "database_id"] = scope == "page" ? PageId : BlockId };
        Assert.Equal(PageId, Assert.Single(await SourceAdapterTests.Read(await harness.Discover("notion", config))).Key);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("is_archived")]
    [InlineData("in_trash")]
    public async Task Notion_archived_and_deleted_pages_are_absent_from_complete_snapshot(string flag)
    {
        using var harness = new Harness();
        var page = JsonSerializer.SerializeToElement(Page(PageId, "Gone")).EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
        page[flag] = true;
        harness.Http.Add("POST", Search, List(page));
        var scan = await harness.Discover("notion", new { token = "private-token" });
        Assert.True(scan.IsComplete);
        Assert.Empty(await SourceAdapterTests.Read(scan));
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("cycle")]
    [InlineData("rate-limit")]
    [InlineData("blocks")]
    [InlineData("depth")]
    [InlineData("items")]
    [InlineData("pages")]
    public async Task Notion_failures_after_first_page_preserve_it_without_title_only_fallback(string fault)
    {
        using var harness = new Harness(items: fault == "items" ? 2 : 1000);
        harness.Http.Add("POST", Search, List(Page(PageId, "First"), more: true, cursor: fault == "cursor" ? null : "next"));
        harness.Http.Add("GET", Children(PageId), List(Block(BlockId, "complete body")));
        var second = "55555555-5555-5555-5555-555555555555";
        var nextSearch = fault == "cycle" ? JsonSerializer.Serialize(new { @object = "list", results = Array.Empty<object>(), has_more = true, next_cursor = "next" }) : List(Page(second, "Second"));
        harness.Http.Add("POST", Search, nextSearch, status: fault == "rate-limit" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        harness.Http.Add("GET", Children(second), List(Block("66666666-6666-6666-6666-666666666666", "incomplete", true)), status: fault == "blocks" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        var scan = await harness.Discover("notion", new { token = "private-token", max_depth = fault == "depth" ? 0 : 8, max_pages = fault == "pages" ? 2 : 100 });
        var keys = new List<string>();
        var error = await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items) { await using var content = item.Content; keys.Add(item.ExternalKey); }
        });
        Assert.Equal(new[] { PageId }, keys);
        Assert.DoesNotContain("private-token", error.ToString());
    }

    [Theory]
    [InlineData("webdav")]
    [InlineData("notion")]
    public async Task Plaintext_or_wrong_sealing_context_never_sends_credentials(string kind)
    {
        using var harness = new Harness();
        object config = kind == "webdav" ? new { base_url = "https://example.test", path = "/docs", username = "user", password = "private-secret" } : (object)new { token = "private-secret" };
        var plain = harness.Source(kind, config, seal: false);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(plain));
        var copied = harness.Source(kind, config);
        copied.Id = Guid.NewGuid();
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(copied));
        Assert.Empty(harness.Http.Requests);
    }

    [Theory]
    [InlineData("webdav", "PROPFIND", "https://example.test/docs/", "http://127.0.0.1/")]
    [InlineData("webdav", "GET", "https://example.test/docs/a.txt", "https://other.test/a.txt")]
    [InlineData("notion", "POST", "https://api.notion.com/v1/search", "https://other.test/")]
    [InlineData("notion", "GET", "https://api.notion.com/v1/blocks/11111111-1111-1111-1111-111111111111/children?page_size=100", "https://api.notion.com:8443/")]
    public async Task Both_discovery_and_content_requests_fail_closed_on_unsafe_redirects(string kind, string method, string uri, string location)
    {
        using var harness = new Harness();
        if (kind == "webdav" && method == "GET") harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry("/docs/a.txt")), "application/xml");
        if (kind == "notion" && method == "GET") harness.Http.Add("POST", Search, List(Page(PageId, "Title")));
        harness.Http.Add(method, uri, "", status: HttpStatusCode.TemporaryRedirect, location: location);
        object config = kind == "webdav" ? new { base_url = "https://example.test", path = "/docs", username = "user", password = "private-password" } : (object)new { token = "private-token" };
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(kind, config));
        Assert.DoesNotContain(harness.Http.Requests, request => request.Uri.AbsoluteUri == location);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PROPFIND")]
    public async Task Webdav_same_origin_redirect_cannot_leave_root_or_discard_prior_items(string method)
    {
        using var harness = new Harness();
        var next = method == "GET" ? Entry("/docs/b.txt") : Entry("/docs/sub/", true);
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry("/docs/a.txt"), next), "application/xml");
        harness.Http.Add("GET", "https://example.test/docs/a.txt", "complete", "text/plain");
        var location = method == "GET" ? "https://example.test/private/b.txt" : "https://example.test/private/";
        harness.Http.Add(method, method == "GET" ? "https://example.test/docs/b.txt" : "https://example.test/docs/sub/", "", status: HttpStatusCode.TemporaryRedirect, location: location);
        harness.Http.Add(method, location, method == "GET" ? "private content" : Dav(Entry("/docs/sub/", true)), "text/plain");
        var scan = await harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs", username = "user", password = "private-password" });
        var keys = new List<string>();
        await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items) { await using var content = item.Content; keys.Add(item.ExternalKey); }
        });
        Assert.False(scan.IsComplete);
        Assert.Equal(new[] { "https://example.test/docs/a.txt" }, keys);
        Assert.DoesNotContain(harness.Http.Requests, request => request.Uri.AbsoluteUri == location);
    }

    [Fact]
    public async Task Webdav_partial_response_without_DAV_length_never_emits_a_partial_file()
    {
        using var harness = new Harness();
        harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry("/docs/a.txt"), Entry("/docs/b.txt")), "application/xml", HttpStatusCode.MultiStatus);
        harness.Http.Add("GET", "https://example.test/docs/a.txt", "complete", "text/plain");
        harness.Http.Add("GET", "https://example.test/docs/b.txt", "partial", "text/plain", HttpStatusCode.PartialContent);
        var scan = await harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" });
        var keys = new List<string>();
        await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items) { await using var content = item.Content; keys.Add(item.ExternalKey); }
        });
        Assert.False(scan.IsComplete);
        Assert.Equal(new[] { "https://example.test/docs/a.txt" }, keys);
    }

    [Theory]
    [InlineData("https://example.test", "/team%20docs/%E4%B8%AD%E6%96%87")]
    [InlineData("https://example.test/team%20docs", "/%E4%B8%AD%E6%96%87")]
    public async Task Webdav_encoded_roots_pass_POST_config_and_download_encoded_filenames(string baseUrl, string path)
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var admin = app.CreateClient();
        await app.AuthenticateAsAsync(admin);
        var ksResponse = await admin.PostAsJsonAsync("/api/knowledge", new { name = "encoded-root", description = "", domain = "" });
        Assert.Equal(HttpStatusCode.OK, ksResponse.StatusCode);
        var ks = (await ksResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await admin.PostAsJsonAsync($"/api/knowledge/{ks}/ingestion-sources", new { kind = "webdav", name = "Encoded", config = new { base_url = baseUrl, path } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var harness = new Harness();
        const string root = "/team%20docs/%E4%B8%AD%E6%96%87/";
        const string file = root + "hello%20%E4%B8%96%E7%95%8C.txt";
        harness.Http.Add("PROPFIND", "https://example.test" + root, Dav(Entry(root, true), Entry(file)), "application/xml", HttpStatusCode.MultiStatus);
        harness.Http.Add("GET", "https://example.test" + file, "complete unicode file", "text/plain");
        var scan = await harness.Discover("webdav", new { base_url = baseUrl, path });
        var item = Assert.Single(await SourceAdapterTests.Read(scan));
        Assert.True(scan.IsComplete);
        Assert.Equal("https://example.test" + file, item.Key);
        Assert.Equal("complete unicode file", item.Body);
    }

    [Theory]
    [InlineData("/docs/%2Fprivate")]
    [InlineData("/docs/%5cprivate")]
    [InlineData("/docs/%2E")]
    [InlineData("/docs/.%2e")]
    [InlineData("/docs/%2520")]
    [InlineData("/docs/%255cprivate")]
    [InlineData("/docs/%zz")]
    public async Task Webdav_ambiguous_encoded_root_is_rejected_before_any_request(string path)
    {
        using var harness = new Harness();
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path }));
        Assert.Empty(harness.Http.Requests);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("pages")]
    [InlineData("items")]
    public async Task Notion_shared_synced_children_render_twice_and_may_retrieve_the_same_subtree_twice(string mode)
    {
        using var harness = new Harness(items: mode == "items" ? 5 : 1000);
        const string second = "33333333-3333-3333-3333-333333333333";
        const string shared = "44444444-4444-4444-4444-444444444444";
        const string leaf = "55555555-5555-5555-5555-555555555555";
        harness.Http.Add("POST", Search, List(Page(PageId, "Shared")));
        var synced = new[]
        {
            new { @object = "block", id = BlockId, type = "synced_block", has_children = true, synced_block = new { synced_from = (object?)null } },
            new { @object = "block", id = second, type = "synced_block", has_children = true, synced_block = new { synced_from = (object?)new { type = "block_id", block_id = BlockId } } },
        };
        harness.Http.Add("GET", Children(PageId), JsonSerializer.Serialize(new { @object = "list", results = synced, has_more = false, next_cursor = (string?)null }));
        harness.Http.Add("GET", Children(BlockId), List(Block(shared, "shared", true)));
        harness.Http.Add("GET", Children(second), List(Block(shared, "shared", true)));
        harness.Http.Add("GET", Children(shared), List(Block(leaf, "leaf")));
        harness.Http.Add("GET", Children(shared), List(Block(leaf, "leaf")));
        if (mode != "complete")
        {
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("notion", new { token = "private-token", max_pages = mode == "pages" ? 5 : 100 }));
            Assert.Equal(1, harness.Http.Requests.Count(request => request.Uri.AbsoluteUri == Children(shared)));
            return;
        }
        var scan = await harness.Discover("notion", new { token = "private-token" });
        var item = Assert.Single(await SourceAdapterTests.Read(scan));
        Assert.True(scan.IsComplete);
        Assert.Equal(PageId, item.Key);
        Assert.Contains("shared\nleaf\nshared\nleaf", item.Body);
        Assert.Equal(2, harness.Http.Requests.Count(request => request.Uri.AbsoluteUri == Children(shared)));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PROPFIND")]
    [InlineData("%2e%2e/private")]
    [InlineData("a%2fb.txt")]
    [InlineData("a%5cb.txt")]
    [InlineData("%252e%252e/private")]
    public async Task Webdav_redirect_scope_still_rejects_ambiguous_paths_and_private_DNS(string target)
    {
        using var harness = new Harness(dns: target is "GET" or "PROPFIND" ? new PrivateDns(target == "GET" ? 3 : 1) : null);
        if (target is "GET" or "PROPFIND")
        {
            if (target == "GET")
            {
                harness.Http.Add("PROPFIND", "https://example.test/docs/", Dav(Entry("/docs/", true), Entry("/docs/a.txt")), "application/xml");
                harness.Http.Add("GET", "https://example.test/docs/a.txt", "", status: HttpStatusCode.TemporaryRedirect, location: "/docs/redirected.txt");
            }
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
            Assert.Equal(target == "GET" ? 2 : 0, harness.Http.Requests.Count);
            Assert.DoesNotContain(harness.Http.Requests, request => request.Uri.AbsolutePath == "/docs/redirected.txt");
            return;
        }
        harness.Http.Add("PROPFIND", "https://example.test/docs/", "", status: HttpStatusCode.TemporaryRedirect, location: "/docs/" + target);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs" }));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Webdav_same_root_redirect_accepts_equivalent_encoded_path_and_keeps_credentials()
    {
        using var harness = new Harness();
        harness.Http.Add("PROPFIND", "https://example.test/docs/", "", status: HttpStatusCode.TemporaryRedirect, location: "/docs/sub%20dir/");
        harness.Http.Add("PROPFIND", "https://example.test/docs/sub%20dir/", Dav(Entry("/docs/", true), Entry("/docs/a.txt")), "application/xml", HttpStatusCode.MultiStatus);
        harness.Http.Add("GET", "https://example.test/docs/a.txt", "", status: HttpStatusCode.TemporaryRedirect, location: "/docs/%61%20b.txt");
        harness.Http.Add("GET", "https://example.test/docs/a%20b.txt", "complete", "text/plain");
        var scan = await harness.Discover("webdav", new { base_url = "https://example.test", path = "/docs", username = "user", password = "private-password" });
        Assert.True(scan.IsComplete);
        Assert.Equal("complete", Assert.Single(await SourceAdapterTests.Read(scan)).Body);
        Assert.Equal(4, harness.Http.Requests.Count);
        Assert.All(harness.Http.Requests, request => Assert.NotNull(request.Authorization));
    }

    internal static string Dav(params string[] entries) => "<d:multistatus xmlns:d='DAV:'>" + string.Concat(entries) + "</d:multistatus>";
    internal static string Entry(string href, bool collection = false) => $"<d:response><d:href>{System.Security.SecurityElement.Escape(href)}</d:href><d:propstat><d:prop><d:resourcetype>{(collection ? "<d:collection/>" : "")}</d:resourcetype><d:getlastmodified>Wed, 02 Sep 2026 15:04:05 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
    internal static object Page(string id, string title, bool archived = false) => new { @object = "page", id, archived, in_trash = false, last_edited_time = "2026-09-29T10:00:00Z", properties = new { Name = new { type = "title", title = new[] { new { plain_text = title } } } } };
    internal static object Block(string id, string text, bool children = false) => new { @object = "block", id, type = "paragraph", has_children = children, paragraph = new { rich_text = new[] { new { plain_text = text } } } };
    internal static string List(object item, bool more = false, string? cursor = null) => JsonSerializer.Serialize(new { @object = "list", results = new[] { item }, has_more = more, next_cursor = cursor });

    internal sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IOptions<SourceNetworkOptions> _options;
        private readonly ISourceDnsResolver _dns;
        public Transport Http { get; } = new();

        public Harness(int bytes = 20 * 1024 * 1024, int items = 1000, ISourceDnsResolver? dns = null)
        {
            _dns = dns ?? new SourceAdapterTests.PublicDns();
            _options = Options.Create(new SourceNetworkOptions { MaxResponseBytes = bytes, MaxItems = items });
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());
            services.AddSourceServices();
            services.AddScoped(provider => new SourceService(null!, new KnowledgeSystemAccessService(), provider.GetRequiredService<SourceAdapterRegistry>(), provider.GetRequiredService<ISourceSecretProtector>(), TimeProvider.System));
            _services = services.BuildServiceProvider();
        }

        public SourceEntity Source(string kind, object config, bool seal = true)
        {
            var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = Guid.NewGuid(), Kind = kind };
            var values = JsonSerializer.SerializeToElement(config).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            if (seal)
            {
                foreach (var field in new[] { "token", "username", "password" }.Where(values.ContainsKey))
                    values[field] = JsonSerializer.SerializeToElement(_services.GetRequiredService<ISourceSecretProtector>().Seal(values[field].GetString()!, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field}"));
            }
            source.Config = JsonSerializer.Serialize(values);
            return source;
        }

        public Task<SourceScan> Discover(string kind, object config) => Discover(Source(kind, config));
        public async Task<SourceScan> Discover(SourceEntity source)
        {
            using var scope = _services.CreateScope();
            var client = new SafeSourceHttpClient(new SourceNetworkPolicy(_dns, _options), Http, _options, TimeProvider.System);
            var service = scope.ServiceProvider.GetRequiredService<SourceService>();
            ISourceAdapter adapter = source.Kind == "webdav" ? new WebDavSourceAdapter(client, service, _options) : new NotionSourceAdapter(client, service, _options);
            return await adapter.DiscoverAsync(source, CancellationToken.None);
        }
        public void Dispose() => _services.Dispose();
    }

    private sealed class PrivateDns(int failAt) : ISourceDnsResolver
    {
        private int _calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult(++_calls >= failAt
                ? new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1") }
                : new[] { IPAddress.Parse("8.8.8.8") });
    }

    internal sealed class Transport : ISourceHttpTransport
    {
        private readonly Dictionary<(string Method, string Uri), Queue<(string Body, string Mime, HttpStatusCode Status, string? Location)>> _responses = new();
        public List<(string Method, Uri Uri, string? Authorization, string? Version, string? Body)> Requests { get; } = [];
        public void Add(string method, string uri, string body, string mime = "application/json", HttpStatusCode status = HttpStatusCode.OK, string? location = null)
        {
            var key = (method, uri);
            if (!_responses.TryGetValue(key, out var queue)) _responses[key] = queue = new();
            queue.Enqueue((body, mime, status, location));
        }
        public async Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
        {
            Assert.Equal(new[] { IPAddress.Parse("8.8.8.8") }, addresses);
            Assert.Empty(request.Headers.IfNoneMatch);
            if (request.Method.Method == "PROPFIND") Assert.Equal("1", Assert.Single(request.Headers.GetValues("Depth")));
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Notion-Version", out var versions) ? versions.Single() : null, body));
            var response = _responses[(request.Method.Method, request.RequestUri!.AbsoluteUri)].Dequeue();
            var message = new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, response.Mime) };
            if (response.Location is not null) message.Headers.Location = new Uri(response.Location, UriKind.RelativeOrAbsolute);
            return new SourceHttpResponse(message);
        }
    }
}