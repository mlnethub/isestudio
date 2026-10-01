using System.Net;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Sources;

public sealed class SourceAdapterTests
{
    [Fact]
    public async Task Url_uses_final_URI_for_media_detection_but_keeps_configured_identity()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/download", "", status: HttpStatusCode.Found, location: "https://example.test/manual.md");
        harness.Http.AddBytes("https://example.test/manual.md", "# Manual"u8.ToArray(), null);
        var item = Assert.Single(await Read(await harness.Discover("url", new { urls = new[] { "https://example.test/download" } })));
        Assert.Equal("https://example.test/download", item.Key);
        Assert.Equal("text/markdown", item.Mime);
        Assert.EndsWith(".md", item.Filename);
    }

    [Fact]
    public async Task GitHub_fake_stream_without_response_metadata_keeps_legacy_page_inference()
    {
        using var harness = new Harness(responseMetadata: false);
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(100));
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=2", "[]");
        var scan = await harness.Discover("github_issues", IssueConfig("github_issues"));
        Assert.True(scan.IsComplete);
        Assert.Equal(100, (await Read(scan)).Count);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task GitHub_last_only_Link_on_full_terminal_page_is_complete_without_more_requests()
    {
        using var harness = new Harness();
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(100),
            link: "<?state=all&per_page=100&page=1>; rel=\"last prev\"");
        var scan = await harness.Discover("github_issues", IssueConfig("github_issues", 1));
        Assert.True(scan.IsComplete);
        Assert.Equal(100, (await Read(scan)).Count);
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData("rss")]
    [InlineData("custom")]
    public async Task Relative_links_use_final_response_URI_after_safe_redirect(string kind)
    {
        using var harness = new Harness();
        var initial = kind == "rss" ? "https://example.test/feed" : "https://example.test/api";
        var final = kind == "rss" ? "https://example.test/news/feed" : "https://example.test/v2/api";
        harness.Http.Add(initial, "", status: HttpStatusCode.Found, location: final);
        harness.Http.Add(final, kind == "rss"
            ? "<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>a</id><link href='article'/></entry><link rel='next' href='next'/></feed>"
            : "{\"items\":[{\"id\":\"a\",\"content\":\"body\"}],\"next_url\":\"next\"}", kind == "rss" ? "application/atom+xml" : "application/json");
        harness.Http.Add("https://example.test/news/article", "body", "text/plain");
        harness.Http.Add(kind == "rss" ? "https://example.test/news/next" : "https://example.test/v2/next",
            kind == "rss" ? "<feed xmlns='http://www.w3.org/2005/Atom'/>" : "{\"items\":[],\"is_complete\":true}",
            kind == "rss" ? "application/atom+xml" : "application/json");
        var scan = await harness.Discover(kind, kind == "rss" ? new { feed_url = initial } : (object)new { endpoint = initial });
        Assert.True(scan.IsComplete);
        Assert.Equal("body", Assert.Single(await Read(scan)).Body);
        Assert.Equal(kind == "rss" ? 4 : 3, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task GitHub_exactly_100_terminal_items_with_real_metadata_need_no_extra_page()
    {
        using var harness = new Harness();
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(100));
        var scan = await harness.Discover("github_issues", IssueConfig("github_issues", 1));
        Assert.True(scan.IsComplete);
        Assert.Equal(100, (await Read(scan)).Count);
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task GitHub_Link_next_controls_pagination_even_for_short_pages()
    {
        using var harness = new Harness();
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(1),
            link: "<?state=all&per_page=100&page=7>; rel=\"next\", <?state=all&per_page=100&page=7>; rel=\"last\"");
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=7", "[]");
        var scan = await harness.Discover("github_issues", IssueConfig("github_issues"));
        Assert.True(scan.IsComplete);
        Assert.Single(await Read(scan));
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("<https://other.test/issues?page=2>; rel=\"next\"")]
    [InlineData("<http://api.github.com/repos/owner/repo/issues?page=2>; rel=\"next\"")]
    [InlineData("<?token=secret>; rel=\"next\"")]
    [InlineData("</repos/other/repo/issues?page=2>; rel=\"next\"")]
    [InlineData("<?page=2>; rel=\"next\", <?page=3>; rel=\"next\"")]
    [InlineData("not-a-link")]
    public async Task GitHub_invalid_Link_fails_before_next_request_and_is_not_complete(string link)
    {
        using var harness = new Harness();
        harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(1), link: link);
        var scan = await harness.Discover("github_issues", IssueConfig("github_issues"));
        Assert.False(scan.IsComplete);
        await Assert.ThrowsAsync<SourceNetworkException>(() => Read(scan));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Default_30_second_deadline_is_shared_across_pages_and_partial_failure_is_observable()
    {
        using var harness = new Harness();
        harness.Http.Delay = TimeSpan.FromSeconds(16);
        harness.Http.Add("https://example.test/api", "{\"items\":[{\"id\":\"a\",\"content\":\"first\"}],\"next_url\":\"/two\"}");
        harness.Http.Add("https://example.test/two", "{\"items\":[{\"id\":\"b\",\"content\":\"second\"}],\"is_complete\":true}");
        var elapsed = Stopwatch.StartNew();
        var scan = await harness.Discover("custom", new { endpoint = "https://example.test/api" });
        Assert.False(scan.IsComplete);
        var keys = new List<string>();
        var error = await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                await using var content = item.Content;
                keys.Add(item.ExternalKey);
            }
        });
        Assert.Equal(new[] { "a" }, keys);
        Assert.Equal("Source discovery timed out.", error.Message);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 27, 35);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("github_issues")]
    [InlineData("jira_issues")]
    public async Task Item_1001_across_pages_fails_after_exactly_1000_successful_items(string kind)
    {
        using var harness = new Harness();
        object config;
        if (kind == "custom")
        {
            harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = Enumerable.Range(0, 1000).Select(index => new { id = $"item-{index}", content = "body" }), next_url = "/two" }));
            harness.Http.Add("https://example.test/two", "{\"items\":[{\"id\":\"item-1000\",\"content\":\"body\"}],\"is_complete\":true}");
            config = new { endpoint = "https://example.test/api" };
        }
        else
        {
            for (var page = 0; page <= 10; page++)
            {
                var count = page == 10 ? 1 : 100;
                if (kind == "github_issues")
                    harness.Http.Add($"https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page={page + 1}", JsonSerializer.Serialize(Enumerable.Range(page * 100, count).Select(index => new { node_id = $"item-{index}", number = index + 1, title = "Issue", body = "body", state = "open", updated_at = "2026-09-29T10:00:00Z" })), link: page < 10 ? GitHubNext(page + 2) : null);
                else
                    harness.Http.Add(JiraUrl(page * 100), JsonSerializer.Serialize(new { startAt = page * 100, maxResults = 100, total = 1001, issues = Enumerable.Range(page * 100, count).Select(index => new { id = $"item-{index}", fields = new { summary = "Issue", description = "body", status = new { name = "Open" }, updated = "2026-09-29T10:00:00Z" } }) }));
            }
            config = IssueConfig(kind);
        }
        var scan = await harness.Discover(kind, config);
        var countRead = 0;
        await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                await using var content = item.Content;
                countRead++;
            }
        });
        Assert.Equal(1000, countRead);
        Assert.Equal(kind == "custom" ? 2 : 11, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("text/html", ".html")]
    [InlineData("text/markdown", ".md")]
    [InlineData("application/json", ".txt")]
    [InlineData("application/xml", ".txt")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    public async Task Extensionless_response_uses_accepted_media_metadata_and_parser_filename(string mime, string extension)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/download", "content without signature", mime);
        var item = Assert.Single(await Read(await harness.Discover("url", new { urls = new[] { "https://example.test/download" } })));
        Assert.Equal(mime, item.Mime);
        Assert.EndsWith(extension, item.Filename);
    }

    [Theory]
    [InlineData(null, "word/document.xml", ".docx")]
    [InlineData("application/octet-stream", "xl/workbook.xml", ".xlsx")]
    public async Task Extensionless_unknown_media_detects_bounded_office_zip_signature(string? mime, string entry, string extension)
    {
        using var archiveBody = new MemoryStream();
        using (var archive = new ZipArchive(archiveBody, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry(entry);
        }
        using var harness = new Harness();
        harness.Http.AddBytes("https://example.test/download", archiveBody.ToArray(), mime);
        var item = Assert.Single(await Read(await harness.Discover("url", new { urls = new[] { "https://example.test/download" } })));
        Assert.EndsWith(extension, item.Filename);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    public async Task Custom_text_formats_use_parser_supported_filenames(string mime)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = new[] { new { id = "a", content = "safe text", mime } }, is_complete = true }));
        Assert.EndsWith(".txt", Assert.Single(await Read(await harness.Discover("custom", new { endpoint = "https://example.test/api" }))).Filename);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public async Task Publisher_keys_match_persistent_character_limit(int length, bool accepted)
    {
        using var harness = new Harness();
        var key = new string('x', length);
        harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = new[] { new { id = key, content = "body" } }, is_complete = true }));
        if (accepted) Assert.Equal(key, Assert.Single(await Read(await harness.Discover("custom", new { endpoint = "https://example.test/api" }))).Key);
        else await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("custom", new { endpoint = "https://example.test/api" }));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("rss")]
    [InlineData("custom")]
    [InlineData("github_issues")]
    [InlineData("jira_issues")]
    public async Task Later_fetch_failure_preserves_prior_items_and_throws_at_enumeration_end(string kind)
    {
        using var harness = new Harness();
        var config = ConfigurePartialFailure(harness.Http, kind);
        var scan = await harness.Discover(kind, config);
        Assert.False(scan.IsComplete);
        var keys = new List<string>();
        var error = await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                await using var content = item.Content;
                keys.Add(item.ExternalKey);
            }
        });
        Assert.Equal(kind == "github_issues" ? 100 : 1, keys.Count);
        Assert.DoesNotContain("secret-upstream", error.ToString());
    }

    internal static object ConfigurePartialFailure(FakeTransport http, string kind)
    {
        const string first = "https://example.test/one";
        const string broken = "https://example.test/two";
        http.Add(broken, "secret-upstream", status: HttpStatusCode.BadGateway);
        switch (kind)
        {
            case "url":
                http.Add(first, "successful body", "text/plain");
                return new { urls = new[] { first, broken } };
            case "rss":
                http.Add("https://example.test/feed", "<rss><channel><item><guid>a</guid><link>https://example.test/one</link></item><item><guid>b</guid><link>https://example.test/two</link></item></channel></rss>", "application/rss+xml");
                http.Add(first, "successful body", "text/plain");
                return new { feed_url = "https://example.test/feed" };
            case "custom":
                http.Add(first, "{\"items\":[{\"id\":\"a\",\"content\":\"successful body\"}],\"next_url\":\"/two\"}");
                return new { endpoint = first };
            case "github_issues":
                http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(100), link: GitHubNext(2));
                http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=2", "secret-upstream", status: HttpStatusCode.BadGateway);
                return IssueConfig(kind);
            default:
                http.Add(JiraUrl(0), "{\"startAt\":0,\"maxResults\":1,\"total\":2,\"issues\":[{\"id\":\"a\",\"fields\":{\"summary\":\"A\",\"status\":{\"name\":\"Open\"},\"updated\":\"2026-09-29T10:00:00Z\"}}]}");
                http.Add(JiraUrl(1), "secret-upstream", status: HttpStatusCode.BadGateway);
                return IssueConfig(kind);
        }
    }

    internal static string GitHubPage(int count, string state = "open") => JsonSerializer.Serialize(
        Enumerable.Range(1, count).Select(index => new { node_id = $"node-{index}", number = index, title = $"Issue {index}", body = "Full body", state,
            created_at = "2026-09-28T10:00:00Z", updated_at = "2026-09-29T10:00:00Z", closed_at = (string?)null }));

    internal static string GitHubNext(int page) => $"<https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page={page}>; rel=\"next\"";

    internal static string JiraUrl(int offset) => "https://example.test/rest/api/2/search?jql="
        + Uri.EscapeDataString("project = TEST ORDER BY id ASC")
        + "&fields=summary,description,status,created,updated,resolutiondate&maxResults=100&startAt=" + offset;

    internal static string JiraCloudUrl(string? token = null) => "https://example.test/rest/api/3/search/jql?jql="
        + Uri.EscapeDataString("project = TEST ORDER BY id ASC")
        + "&fields=summary,description,status,created,updated,resolutiondate&maxResults=100"
        + (token is null ? "" : "&nextPageToken=" + Uri.EscapeDataString(token));

    [Fact]
    public async Task Jira_cloud_uses_enhanced_GET_search_escaped_token_and_renders_ADF()
    {
        using var harness = new Harness();
        const string token = "opaque+/=&?token";
        harness.Http.Add(JiraCloudUrl(), """{"isLast":false,"nextPageToken":"opaque+/=&?token","issues":[{"id":"10001","key":"TEST-1","fields":{"summary":"Cloud issue","status":{"name":"Open"},"updated":"2026-09-29T12:00:00+0200","description":{"type":"doc","version":1,"content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Details"}]},{"type":"paragraph","content":[{"type":"text","text":"Full body","marks":[{"type":"strong"}]},{"type":"hardBreak"},{"type":"text","text":"Second line"}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Action item"}]}]}]}]}}}]}""");
        harness.Http.Add(JiraCloudUrl(token), "{\"isLast\":true,\"issues\":[]}");
        var scan = await harness.Discover("jira_issues", new { base_url = "https://example.test", project = "TEST", deployment = "cloud" });
        Assert.True(scan.IsComplete);
        var item = Assert.Single(await Read(scan));
        Assert.Equal("10001", item.Key);
        Assert.Contains("## Details", item.Body);
        Assert.Contains("**Full body**", item.Body);
        Assert.Contains("Second line", item.Body);
        Assert.Contains("- Action item", item.Body);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:00Z"), item.Time);
        Assert.Equal(new[] { JiraCloudUrl(), JiraCloudUrl(token) }, harness.Http.Requests.Select(request => request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task Jira_cloud_terminal_exactly_100_items_with_one_page_budget_is_complete()
    {
        using var harness = new Harness();
        harness.Http.Add(JiraCloudUrl(), JsonSerializer.Serialize(new { isLast = true, issues = Enumerable.Range(0, 100).Select(index => new
        { id = $"id-{index}", fields = new { summary = "Issue", description = (string?)null, status = new { name = "Open" }, updated = "2026-09-29T10:00:00Z" } }) }));
        var scan = await harness.Discover("jira_issues", new { base_url = "https://example.test", project = "TEST", deployment = "cloud", max_pages = 1 });
        Assert.True(scan.IsComplete);
        Assert.Equal(100, (await Read(scan)).Count);
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData("{\"issues\":[]}")]
    [InlineData("{\"isLast\":false,\"issues\":[]}")]
    [InlineData("{\"isLast\":false,\"nextPageToken\":\"\",\"issues\":[]}")]
    [InlineData("{\"isLast\":false,\"nextPageToken\":42,\"issues\":[]}")]
    [InlineData("{\"isLast\":\"true\",\"issues\":[]}")]
    [InlineData("{\"isLast\":true,\"issues\":{}}")]
    public async Task Jira_cloud_invalid_pagination_never_completes(string body)
    {
        using var harness = new Harness();
        harness.Http.Add(JiraCloudUrl(), body);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues",
            new { base_url = "https://example.test", project = "TEST", deployment = "cloud" }));
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jira_cloud_partial_failure_or_page_budget_yields_prior_item_then_throws(bool pageBudget)
    {
        using var harness = new Harness();
        harness.Http.Add(JiraCloudUrl(), """{"isLast":false,"nextPageToken":"next","issues":[{"id":"a","fields":{"summary":"A","description":null,"status":{"name":"Open"},"updated":"2026-09-29T10:00:00Z"}}]}""");
        harness.Http.Add(JiraCloudUrl("next"), "secret-error", status: HttpStatusCode.BadGateway);
        var scan = await harness.Discover("jira_issues", new { base_url = "https://example.test", project = "TEST", deployment = "cloud", max_pages = pageBudget ? 1 : 100 });
        Assert.False(scan.IsComplete);
        var count = 0;
        var error = await Assert.ThrowsAsync<SourceNetworkException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                await using var content = item.Content;
                Assert.Equal("a", item.ExternalKey);
                count++;
            }
        });
        Assert.Equal(1, count);
        Assert.DoesNotContain("secret-error", error.ToString());
        Assert.Equal(pageBudget ? 1 : 2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Jira_explicit_data_center_preserves_v2_contract()
    {
        using var harness = new Harness();
        harness.Http.Add(JiraUrl(0), "{\"startAt\":0,\"maxResults\":100,\"total\":0,\"issues\":[]}");
        Assert.True((await harness.Discover("jira_issues", new { base_url = "https://example.test", project = "TEST", deployment = "data_center" })).IsComplete);
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("Cloud")]
    [InlineData(42)]
    [InlineData(null)]
    public async Task Jira_invalid_deployment_fails_before_HTTP(object? deployment)
    {
        using var harness = new Harness();
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues",
            new { base_url = "https://example.test", project = "TEST", deployment }));
        Assert.Empty(harness.Http.Requests);
    }

    [Theory]
    [InlineData("bad-time")]
    [InlineData("2026-02-30T10:00:00Z")]
    public async Task Jira_cloud_invalid_time_is_not_accepted(string updated)
    {
        using var harness = new Harness();
        harness.Http.Add(JiraCloudUrl(), JsonSerializer.Serialize(new { isLast = true, issues = new[] { new
        { id = "a", fields = new { summary = "Issue", status = new { name = "Open" }, updated } } } }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues",
            new { base_url = "https://example.test", project = "TEST", deployment = "cloud" }));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Jira_cloud_token_cycle_stops_before_repeating_a_request()
    {
        using var harness = new Harness();
        const string body = "{\"isLast\":false,\"nextPageToken\":\"repeat\",\"issues\":[]}";
        harness.Http.Add(JiraCloudUrl(), body);
        harness.Http.Add(JiraCloudUrl("repeat"), body);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues",
            new { base_url = "https://example.test", project = "TEST", deployment = "cloud" }));
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData(401, null)]
    [InlineData(429, null)]
    [InlineData(302, "http://127.0.0.1/private")]
    [InlineData(302, "https://other.test/private")]
    public async Task Jira_cloud_sealed_auth_and_redirect_limits_match_data_center(int status, string? location)
    {
        using var harness = new Harness(encryptionKey: Convert.ToBase64String(new byte[32]));
        harness.Http.Add(JiraCloudUrl(), "cloud-secret", status: (HttpStatusCode)status, location: location);
        var source = harness.SealedSource("jira_issues", new
        { base_url = "https://example.test", project = "TEST", deployment = "cloud", auth_header = "Bearer cloud-secret" });
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(source));
        Assert.DoesNotContain("cloud-secret", error.ToString());
        Assert.Equal("Bearer cloud-secret", Assert.Single(harness.Http.Requests).Authorization);
    }

    [Theory]
    [InlineData("{\"type\":\"doc\",\"version\":2,\"content\":[]}")]
    [InlineData("{\"type\":\"doc\",\"version\":1,\"content\":{}}")]
    [InlineData("{\"type\":\"doc\",\"version\":1,\"content\":[{\"type\":\"unknown\",\"text\":\"do not drop\"}]}")]
    public async Task Jira_cloud_unusable_ADF_fails_instead_of_silently_losing_text(string adf)
    {
        using var harness = new Harness();
        using var document = JsonDocument.Parse(adf);
        harness.Http.Add(JiraCloudUrl(), JsonSerializer.Serialize(new { isLast = true, issues = new[] { new
        { id = "a", fields = new { summary = "Issue", description = document.RootElement, status = new { name = "Open" }, updated = "2026-09-29T10:00:00Z" } } } }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues",
            new { base_url = "https://example.test", project = "TEST", deployment = "cloud" }));
    }

    [Theory]
    [InlineData("github_issues")]
    [InlineData("jira_issues")]
    public async Task Issues_enumerate_all_pages_with_immutable_ids_body_state_and_timestamps(string kind)
    {
        using var harness = new Harness();
        if (kind == "github_issues")
        {
            harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1", GitHubPage(100), link: GitHubNext(2));
            harness.Http.Add("https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=2", "[]");
        }
        else
        {
            harness.Http.Add(JiraUrl(0), """{"startAt":0,"maxResults":1,"total":2,"issues":[{"id":123,"key":"TEST-1","fields":{"summary":"Issue","description":"Full body","status":{"name":"Closed"},"created":"2026-09-28T10:00:00.000+0000","updated":"2026-09-29T10:00:00.944+0000"}}]}""");
            harness.Http.Add(JiraUrl(1), """{"startAt":1,"maxResults":1,"total":2,"issues":[{"id":"124","key":"MOVED-9","fields":{"summary":"Other","description":null,"status":{"name":"Open"},"created":"2026-09-28T10:00:00Z","updated":"2026-09-29T10:00:00Z"}}]}""");
        }
        var scan = await harness.Discover(kind, IssueConfig(kind));
        Assert.True(scan.IsComplete);
        var items = await Read(scan);
        Assert.Equal(kind == "github_issues" ? 100 : 2, items.Count);
        Assert.Equal(kind == "github_issues" ? "node-1" : "123", items[0].Key);
        Assert.Contains("Full body", items[0].Body);
        Assert.Contains(kind == "github_issues" ? "open" : "Closed", items[0].Body);
        Assert.Contains("2026-09-28", items[0].Body);
        Assert.Equal("text/markdown", items[0].Mime);
        Assert.Equal(DateTimeOffset.Parse(kind == "github_issues" ? "2026-09-29T10:00:00Z" : "2026-09-29T10:00:00.944Z"), items[0].Time);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    internal static object IssueConfig(string kind, int maxPages = 100) => kind == "github_issues"
        ? new { repo = "owner/repo", max_pages = maxPages }
        : (object)new { base_url = "https://example.test", project = "TEST", max_pages = maxPages };

    [Theory]
    [InlineData("github_issues", 401)]
    [InlineData("jira_issues", 401)]
    [InlineData("github_issues", 302)]
    [InlineData("jira_issues", 302)]
    public async Task Issues_sealed_auth_and_unsafe_redirect_failures_are_sanitized(string kind, int status)
    {
        using var harness = new Harness(encryptionKey: Convert.ToBase64String(new byte[32]));
        var values = JsonSerializer.SerializeToElement(IssueConfig(kind)).EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
        values["auth_header"] = "Bearer task3-secret";
        var url = kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1" : JiraUrl(0);
        harness.Http.Add(url, "task3-secret", status: (HttpStatusCode)status, location: status == 302 ? "http://127.0.0.1/private" : null);
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(harness.SealedSource(kind, values)));
        Assert.DoesNotContain("task3-secret", error.ToString());
        Assert.Equal("Bearer task3-secret", Assert.Single(harness.Http.Requests).Authorization);
    }

    [Theory]
    [InlineData("github_issues", 304)]
    [InlineData("github_issues", 429)]
    [InlineData("github_issues", 502)]
    [InlineData("jira_issues", 304)]
    [InlineData("jira_issues", 429)]
    [InlineData("jira_issues", 502)]
    public async Task Issues_failing_second_page_never_returns_a_complete_scan(string kind, int status)
    {
        using var harness = new Harness();
        harness.Http.Add(kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1" : JiraUrl(0),
            kind == "github_issues" ? GitHubPage(100) : """{"startAt":0,"maxResults":1,"total":2,"issues":[{"id":"a","fields":{"summary":"A","status":{"name":"Open"},"updated":"2026-09-29T10:00:00Z"}}]}""", link: kind == "github_issues" ? GitHubNext(2) : null);
        harness.Http.Add(kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=2" : JiraUrl(1), "secret error", status: (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead(kind, IssueConfig(kind)));
        Assert.DoesNotContain("secret error", error.ToString());
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("github_issues", "[{\"number\":1,\"title\":\"Title\",\"state\":\"open\"}]")]
    [InlineData("github_issues", "[{\"node_id\":\"\",\"number\":1}]")]
    [InlineData("github_issues", "[{\"node_id\":42}]")]
    [InlineData("jira_issues", "{\"startAt\":0,\"total\":1,\"maxResults\":100,\"issues\":[{\"key\":\"TEST-1\"}]}")]
    [InlineData("jira_issues", "{\"startAt\":0,\"total\":1,\"maxResults\":100,\"issues\":[{\"id\":\"\"}]}")]
    public async Task Issues_never_fallback_to_display_identity(string kind, string body)
    {
        using var harness = new Harness();
        harness.Http.Add(kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1" : JiraUrl(0), body);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(kind, IssueConfig(kind)));
    }

    [Theory]
    [InlineData("github_issues")]
    [InlineData("jira_issues")]
    public async Task Issues_page_and_item_budgets_fail_instead_of_truncating(string kind)
    {
        foreach (var itemLimit in new[] { 1, 1000 })
        {
            using var harness = new Harness(maxItems: itemLimit);
            harness.Http.Add(kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1" : JiraUrl(0),
                kind == "github_issues" ? GitHubPage(100) : """{"startAt":0,"maxResults":2,"total":3,"issues":[{"id":"a","fields":{"summary":"A","status":{"name":"Open"},"updated":"2026-09-29T10:00:00Z"}},{"id":"b","fields":{"summary":"B","status":{"name":"Open"},"updated":"2026-09-29T10:00:00Z"}}]}""", link: kind == "github_issues" ? GitHubNext(2) : null);
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead(kind, IssueConfig(kind, 1)));
            Assert.Single(harness.Http.Requests);
        }
    }

    [Theory]
    [InlineData("github_issues")]
    [InlineData("jira_issues")]
    public async Task Issues_duplicate_immutable_ids_are_rejected(string kind)
    {
        using var harness = new Harness();
        var body = kind == "github_issues" ? GitHubPage(2).Replace("node-2", "node-1", StringComparison.Ordinal)
            : """{"startAt":0,"maxResults":100,"total":2,"issues":[{"id":123,"key":"TEST-1","fields":{"summary":"A","status":{"name":"Open"},"updated":"2026-09-29T10:00:00Z"}},{"id":"123","key":"MOVED-2","fields":{"summary":"B","status":{"name":"Closed"},"updated":"2026-09-29T10:00:00Z"}}]}""";
        harness.Http.Add(kind == "github_issues" ? "https://api.github.com/repos/owner/repo/issues?state=all&per_page=100&page=1" : JiraUrl(0), body);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead(kind, IssueConfig(kind)));
    }

    [Theory]
    [InlineData("{\"startAt\":0,\"maxResults\":100,\"total\":1,\"issues\":[]}")]
    [InlineData("{\"startAt\":1,\"maxResults\":100,\"total\":0,\"issues\":[]}")]
    [InlineData("{\"startAt\":0,\"maxResults\":100,\"total\":1,\"issues\":[{\"id\":null}]}")]
    [InlineData("{\"startAt\":0,\"maxResults\":100,\"total\":1,\"issues\":[{\"id\":{},\"key\":\"TEST-1\"}]}")]
    public async Task Jira_missing_pages_or_invalid_id_types_never_complete(string body)
    {
        using var harness = new Harness();
        harness.Http.Add(JiraUrl(0), body);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("jira_issues", IssueConfig("jira_issues")));
    }

    [Fact]
    public async Task Url_normalizes_identity_and_returns_original_page_bytes()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/page", "<html>original</html>", "text/html");
        var scan = await harness.Discover("url", new { urls = new[] { "HTTPS://EXAMPLE.TEST:443/page#section" } });
        Assert.True(scan.IsComplete);
        var item = Assert.Single(await Read(scan));
        Assert.Equal("https://example.test/page", item.Key);
        Assert.Equal("<html>original</html>", item.Body);
        Assert.EndsWith(".html", item.Filename);
        Assert.Null(item.Time);
    }

    [Theory]
    [InlineData("rss", "<rss version='2.0'><channel><item><guid>stable-guid</guid><title>Article</title><link>https://example.test/article</link><description>Summary only</description><pubDate>Tue, 29 Sep 2026 10:00:00 GMT</pubDate></item></channel></rss>")]
    [InlineData("atom", "<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>stable-guid</id><title>Article</title><link rel='self' href='https://example.test/not-body'/><link rel='alternate' href='/article'/><summary>Summary only</summary><updated>2026-09-29T10:00:00Z</updated></entry></feed>")]
    public async Task Rss_and_atom_fetch_full_body_and_preserve_publisher_identity_and_time(string format, string feed)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/feed", feed, format == "rss" ? "application/rss+xml" : "application/atom+xml");
        harness.Http.Add("https://example.test/article", "<html>Full article, not summary</html>", "text/html");
        var scan = await harness.Discover("rss", new { feed_url = "https://example.test/feed" });
        var item = Assert.Single(await Read(scan));
        Assert.True(scan.IsComplete);
        Assert.Equal("stable-guid", item.Key);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:00Z"), item.Time);
        Assert.Equal("<html>Full article, not summary</html>", item.Body);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Rss_missing_guid_uses_normalized_link_and_preserves_content_query()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/feed", "<rss><channel><item><link>HTTPS://EXAMPLE.TEST:443/article?id=42#top</link></item></channel></rss>", "application/xml");
        harness.Http.Add("https://example.test/article?id=42", "body", "text/html");
        var item = Assert.Single(await Read(await harness.Discover("rss", new { feed_url = "https://example.test/feed" })));
        Assert.Equal("https://example.test/article?id=42", item.Key);
    }

    [Theory]
    [InlineData("<rss><channel><item><guid>one</guid><description>Summary</description></item></channel></rss>")]
    [InlineData("<rss><channel><item><link>file:///secret</link></item></channel></rss>")]
    [InlineData("<rss><channel><item><link>http://127.0.0.1/body</link></item></channel></rss>")]
    [InlineData("<!DOCTYPE rss [<!ENTITY secret SYSTEM 'file:///secret'>]><rss><channel>&secret;</channel></rss>")]
    [InlineData("<rss><channel><item></channel></rss>")]
    [InlineData("<not-a-feed/>")]
    public async Task Rss_rejects_unusable_items_unsafe_links_and_invalid_xml(string feed)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/feed", feed, "application/xml");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("rss", new { feed_url = "https://example.test/feed" }));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Rss_body_failure_fails_the_entire_scan_without_summary_fallback()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/feed", "<rss><channel><item><guid>one</guid><link>https://example.test/one</link></item><item><guid>two</guid><link>https://example.test/two</link><description>fallback forbidden</description></item></channel></rss>", "application/xml");
        harness.Http.Add("https://example.test/one", "full", "text/html");
        harness.Http.Add("https://example.test/two", "secret upstream error", "text/html", HttpStatusCode.InternalServerError);
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("rss", new { feed_url = "https://example.test/feed" }));
        Assert.DoesNotContain("secret upstream error", error.ToString());
    }

    [Fact]
    public async Task Custom_follows_all_pages_plain_ids_times_and_explicit_completion()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", """{"items":[{"id":"plain-id","title":"Article","content":"first","doc_time":"2026-09-29T10:00:00Z","mime":"text/markdown"}],"is_complete":false,"next_url":"/page/2"}""");
        harness.Http.Add("https://example.test/page/2", """{"items":[{"id":"second","content":"second"}],"is_complete":true,"deleted":["plain-id"]}""");
        var scan = await harness.Discover("custom", new { endpoint = "https://example.test/api" });
        var items = await Read(scan);
        Assert.True(scan.IsComplete);
        Assert.Equal(new[] { "plain-id", "second" }, items.Select(item => item.Key));
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:00Z"), items[0].Time);
        Assert.Equal("first", items[0].Body);
        Assert.Equal("text/markdown", items[0].Mime);
        Assert.EndsWith(".md", items[0].Filename);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[],\"is_complete\":false}")]
    [InlineData("{\"items\":[],\"deleted\":[\"other\"]}")]
    public async Task Custom_without_declared_full_completion_cannot_reconcile(string body)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", body);
        Assert.False((await harness.Discover("custom", new { endpoint = "https://example.test/api" })).IsComplete);
    }

    [Theory]
    [InlineData("{\"items\":[{\"id\":\"\",\"content\":\"body\"}],\"is_complete\":true}")]
    [InlineData("{\"items\":[{\"id\":42,\"content\":\"body\"}],\"is_complete\":true}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"content\":\" \"}],\"is_complete\":true}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"content\":{} }]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"content\":\"body\",\"doc_time\":\"wrong\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"content\":\"body\",\"mime\":\"image/unknown\"}]}")]
    [InlineData("{\"items\":[],\"is_complete\":\"true\"}")]
    [InlineData("{\"items\":[],\"is_complete\":true,\"next_url\":\"/second\"}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":[]")]
    [InlineData("{\"items\":[],\"items\":[]}")]
    [InlineData("{\"items\":[{\"id\":null,\"content\":\"body\"}]}")]
    [InlineData("{\"items\":[{\"content\":\"body\"}]}")]
    public async Task Custom_invalid_schema_fails_with_sanitized_errors(string body)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", body);
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("custom", new { endpoint = "https://example.test/api" }));
        Assert.DoesNotContain(body, error.ToString());
    }

    [Theory]
    [InlineData("https://other.test/page")]
    [InlineData("http://example.test/page")]
    [InlineData("https://example.test:8443/page")]
    [InlineData("https://user:secret@example.test/page")]
    [InlineData("/page?token=secret")]
    public async Task Custom_continuation_cannot_change_origin_or_carry_credentials(string continuation)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = Array.Empty<object>(), is_complete = false, next_url = continuation }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("custom", new { endpoint = "https://example.test/api" }));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Custom_second_page_failure_and_cycles_are_not_complete()
    {
        foreach (var next in new[] { "/broken", "/api" })
        {
            using var harness = new Harness();
            harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = new[] { new { id = "one", content = "body" } }, is_complete = false, next_url = next }));
            harness.Http.Add("https://example.test/broken", "do not log", status: HttpStatusCode.BadGateway);
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("custom", new { endpoint = "https://example.test/api" }));
        }
    }

    [Theory]
    [InlineData("url")]
    [InlineData("rss")]
    [InlineData("custom")]
    public async Task Duplicate_stable_keys_are_rejected(string kind)
    {
        using var harness = new Harness();
        object config;
        if (kind == "url")
        {
            config = new { urls = new[] { "https://example.test/api", "HTTPS://EXAMPLE.TEST:443/api#same" } };
            harness.Http.Add("https://example.test/api", "body", "text/html");
        }
        else if (kind == "rss")
        {
            config = new { feed_url = "https://example.test/api" };
            harness.Http.Add("https://example.test/api", "<rss><channel><item><guid>same</guid><link>https://example.test/one</link></item><item><guid>same</guid><link>https://example.test/two</link></item></channel></rss>", "application/xml");
            harness.Http.Add("https://example.test/one", "body", "text/html");
            harness.Http.Add("https://example.test/two", "body", "text/html");
        }
        else
        {
            config = new { endpoint = "https://example.test/api" };
            harness.Http.Add("https://example.test/api", """{"items":[{"id":"same","content":"body"}],"is_complete":false,"next_url":"/two"}""");
            harness.Http.Add("https://example.test/two", """{"items":[{"id":"same","content":"body"}],"is_complete":true}""");
        }
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead(kind, config));
    }

    [Fact]
    public async Task Body_and_item_budgets_are_shared_across_the_scan()
    {
        using (var harness = new Harness(maxBytes: 8))
        {
            harness.Http.Add("https://example.test/one", "12345", "text/html");
            harness.Http.Add("https://example.test/two", "67890", "text/html");
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("url", new { urls = new[] { "https://example.test/one", "https://example.test/two" } }));
        }
        using (var harness = new Harness(maxItems: 1))
        {
            harness.Http.Add("https://example.test/api", """{"items":[{"id":"one","content":"body"}],"next_url":"/two"}""");
            harness.Http.Add("https://example.test/two", """{"items":[{"id":"two","content":"body"}],"is_complete":true}""");
            await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("custom", new { endpoint = "https://example.test/api" }));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789")]
    public async Task Url_empty_or_oversize_body_is_rejected(string body)
    {
        using var harness = new Harness(maxBytes: 8);
        harness.Http.Add("https://example.test/api", body, "text/html");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("url", new { urls = new[] { "https://example.test/api" } }));
    }

    [Theory]
    [InlineData("rss")]
    [InlineData("custom")]
    public async Task Feed_and_custom_page_limits_apply_across_all_pages(string kind)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", kind == "rss"
            ? "<feed xmlns='http://www.w3.org/2005/Atom'><link rel='next' href='/second'/></feed>"
            : "{\"items\":[],\"next_url\":\"/second\"}", kind == "rss" ? "application/atom+xml" : "application/json");
        object config = kind == "rss" ? new { feed_url = "https://example.test/api", max_pages = 1 } : new { endpoint = "https://example.test/api", max_pages = 1 };
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(kind, config));
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Url_page_count_is_bounded_for_the_whole_scan()
    {
        using var harness = new Harness();
        var urls = Enumerable.Range(0, 101).Select(index => $"https://example.test/page/{index}").ToArray();
        foreach (var url in urls) harness.Http.Add(url, "body", "text/html");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.DiscoverAndRead("url", new { urls }));
        Assert.Equal(100, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Custom_total_bytes_include_every_page_even_when_each_response_fits()
    {
        const string first = "{\"items\":[],\"next_url\":\"/second\"}";
        const string second = "{\"items\":[{\"id\":\"a\",\"content\":\"full article\"}],\"is_complete\":true}";
        using var harness = new Harness(maxBytes: Encoding.UTF8.GetByteCount(first) + Encoding.UTF8.GetByteCount(second) - 1);
        harness.Http.Add("https://example.test/api", first);
        harness.Http.Add("https://example.test/second", second);
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("custom", new { endpoint = "https://example.test/api" }));
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Rss_total_bytes_include_feed_and_all_article_bodies()
    {
        var feed = "<rss><channel><item><link>https://example.test/body</link></item></channel></rss>";
        using var harness = new Harness(maxBytes: Encoding.UTF8.GetByteCount(feed) + 4);
        harness.Http.Add("https://example.test/feed", feed, "application/rss+xml");
        harness.Http.Add("https://example.test/body", "full body", "text/html");
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("rss", new { feed_url = "https://example.test/feed" }));
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Custom_numeric_same_origin_continuation_is_safe_and_explicit_end_is_required()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", "{\"items\":[],\"next_url\":\"?page=2\"}");
        harness.Http.Add("https://example.test/api?page=2", "{\"items\":[],\"is_complete\":true}");
        Assert.True((await harness.Discover("custom", new { endpoint = "https://example.test/api" })).IsComplete);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("rss")]
    [InlineData("custom")]
    public async Task Missing_schema_and_plaintext_auth_fail_before_http(string kind)
    {
        using var harness = new Harness();
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(kind, new { }));
        object config = kind switch
        {
            "url" => new { urls = new[] { "https://example.test/api" }, auth_header = "Bearer plaintext-secret" },
            "rss" => new { feed_url = "https://example.test/api", auth_header = "Bearer plaintext-secret" },
            _ => new { endpoint = "https://example.test/api", auth_header = "Bearer plaintext-secret" },
        };
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(kind, config));
        Assert.DoesNotContain("plaintext-secret", error.ToString());
        Assert.Empty(harness.Http.Requests);
    }

    [Fact]
    public async Task Atom_respects_xml_base_for_article_and_feed_continuation_links()
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/feed", "<feed xmlns='http://www.w3.org/2005/Atom' xml:base='/news/'><entry xml:base='stories/'><id>base-id</id><link href='article'/></entry><link rel='next' href='page2'/></feed>", "application/atom+xml");
        harness.Http.Add("https://example.test/news/stories/article", "article body", "text/html");
        harness.Http.Add("https://example.test/news/page2", "<feed xmlns='http://www.w3.org/2005/Atom'/>", "application/atom+xml");
        var scan = await harness.Discover("rss", new { feed_url = "https://example.test/feed" });
        Assert.True(scan.IsComplete);
        Assert.Equal("base-id", Assert.Single(await Read(scan)).Key);
        Assert.Equal(3, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("rss")]
    [InlineData("custom")]
    public async Task Sealed_auth_is_sent_only_over_https_and_never_as_url_credentials(string kind)
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        using var harness = new Harness(encryptionKey: key);
        object config = kind switch
        {
            "url" => new { urls = new[] { "https://example.test/api" }, auth_header = "Bearer sealed-secret" },
            "rss" => new { feed_url = "https://example.test/api", auth_header = "Bearer sealed-secret" },
            _ => new { endpoint = "https://example.test/api", auth_header = "Bearer sealed-secret" },
        };
        harness.Http.Add("https://example.test/api", kind switch
        {
            "url" => "<html>body</html>",
            "rss" => "<rss><channel><item><guid>one</guid><link>https://other.test/article</link></item><item><guid>two</guid><link>https://example.test/article</link></item></channel></rss>",
            _ => "{\"items\":[],\"next_url\":\"/second\"}",
        }, kind == "rss" ? "application/rss+xml" : kind == "url" ? "text/html" : "application/json");
        harness.Http.Add("https://other.test/article", "public body", "text/html");
        harness.Http.Add("https://example.test/article", "private body", "text/html");
        harness.Http.Add("https://example.test/second", "{\"items\":[],\"is_complete\":true}");
        var source = harness.SealedSource(kind, config);
        await Read(await harness.Discover(source));
        Assert.Equal("Bearer sealed-secret", harness.Http.Requests[0].Authorization);
        Assert.All(harness.Http.Requests.Where(request => request.Uri.Host == "example.test"), request => Assert.Equal("Bearer sealed-secret", request.Authorization));
        Assert.All(harness.Http.Requests.Where(request => request.Uri.Host != "example.test"), request => Assert.Null(request.Authorization));
        source.Config = source.Config.Replace("https://example.test/api", "http://example.test/api", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover(source));
        Assert.DoesNotContain("sealed-secret", error.ToString());
    }

    [Theory]
    [InlineData("https://@example.test/article")]
    [InlineData("//@example.test/article")]
    public async Task Upstream_links_cannot_hide_empty_userinfo(string link)
    {
        using var harness = new Harness();
        harness.Http.Add("https://example.test/api", JsonSerializer.Serialize(new { items = Array.Empty<object>(), next_url = link }));
        await Assert.ThrowsAsync<SourceNetworkException>(() => harness.Discover("custom", new { endpoint = "https://example.test/api" }));
        Assert.Single(harness.Http.Requests);
    }

    internal static async Task<List<(string Key, string Body, string Filename, string? Mime, DateTimeOffset? Time)>> Read(SourceScan scan)
    {
        var items = new List<(string, string, string, string?, DateTimeOffset?)>();
        await foreach (var item in scan.Items)
        {
            await using var content = item.Content;
            using var reader = new StreamReader(content);
            items.Add((item.ExternalKey, await reader.ReadToEndAsync(), item.Filename, item.Mime, item.DocTime));
        }
        return items;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        public FakeTransport Http { get; } = new();

        public Harness(int maxBytes = 20 * 1024 * 1024, int maxItems = 1000, string? encryptionKey = null, bool responseMetadata = true)
        {
            var options = Options.Create(new SourceNetworkOptions { MaxResponseBytes = maxBytes, MaxItems = maxItems });
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SourceSecretProtector.ConfigurationKey] = encryptionKey }).Build());
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<ISEStudioDbContext>(_ => null!);
            services.AddSingleton<KnowledgeSystemAccessService>();
            services.AddSingleton(options);
            var client = new SafeSourceHttpClient(new SourceNetworkPolicy(new PublicDns(), options), Http, options, TimeProvider.System);
            services.AddSingleton<ISafeSourceHttpClient>(responseMetadata ? client : new MetadataFreeClient(client));
            services.AddSourceServices();
            services.AddScoped(provider => new SourceService(null!, new KnowledgeSystemAccessService(),
                provider.GetRequiredService<SourceAdapterRegistry>(), provider.GetRequiredService<ISourceSecretProtector>(), TimeProvider.System));
            _services = services.BuildServiceProvider();
        }

        public async Task<SourceScan> Discover(string kind, object config)
            => await Discover(new SourceEntity { Kind = kind, Config = JsonSerializer.Serialize(config) });

        public async Task DiscoverAndRead(string kind, object config)
            => await Read(await Discover(kind, config));

        public SourceEntity SealedSource(string kind, object config)
        {
            var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = Guid.NewGuid(), Kind = kind };
            var values = JsonSerializer.SerializeToElement(config).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            values["auth_header"] = JsonSerializer.SerializeToElement(_services.GetRequiredService<ISourceSecretProtector>().Seal(values["auth_header"].GetString()!, $"{source.KnowledgeSystemId:D}:{source.Id:D}:auth_header"));
            source.Config = JsonSerializer.Serialize(values);
            return source;
        }

        public async Task<SourceScan> Discover(SourceEntity source)
        {
            using var scope = _services.CreateScope();
            var adapter = Assert.Single(scope.ServiceProvider.GetServices<ISourceAdapter>(), adapter => adapter.Kind == source.Kind);
            return await adapter.DiscoverAsync(source, CancellationToken.None);
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class MetadataFreeClient(ISafeSourceHttpClient inner) : ISafeSourceHttpClient
    {
        public async Task<Stream> GetAsync(Uri uri, SourceRequestOptions options, CancellationToken ct)
        {
            await using var response = await inner.GetAsync(uri, options, ct);
            var stream = new MemoryStream();
            await response.CopyToAsync(stream, ct);
            stream.Position = 0;
            return stream;
        }

        public Task<Stream> PropFindAsync(Uri uri, int depth, SourceRequestOptions options, CancellationToken ct)
            => inner.PropFindAsync(uri, depth, options, ct);
    }

    internal sealed class PublicDns : ISourceDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
    }

    internal sealed class FakeTransport : ISourceHttpTransport
    {
        private readonly Dictionary<string, (string Body, string Mime, HttpStatusCode Status, string? Location, string? Link)> _responses = new();
        private readonly Dictionary<string, (byte[] Body, string? Mime)> _binary = new();
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];
        public TimeSpan Delay { get; set; }

        public void AddBytes(string url, byte[] body, string? mime) => _binary[url] = (body, mime);

        public void Add(string url, string body, string mime = "application/json", HttpStatusCode status = HttpStatusCode.OK, string? location = null, string? link = null)
            => _responses[url] = (body, mime, status, location, link);

        public async Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new[] { IPAddress.Parse("8.8.8.8") }, addresses);
            Assert.Null(request.Headers.IfNoneMatch.FirstOrDefault());
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (_binary.TryGetValue(request.RequestUri!.AbsoluteUri, out var binary))
            {
                var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(binary.Body) };
                if (binary.Mime is not null) result.Content.Headers.ContentType = new(binary.Mime);
                return new SourceHttpResponse(result);
            }
            var response = _responses[request.RequestUri!.AbsoluteUri];
            var message = new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, response.Mime),
            };
            if (response.Location is not null) message.Headers.Location = new Uri(response.Location);
            if (response.Link is not null) message.Headers.TryAddWithoutValidation("Link", response.Link);
            return new SourceHttpResponse(message);
        }
    }
}