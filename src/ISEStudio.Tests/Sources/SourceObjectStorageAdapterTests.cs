using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Sources.Adapters;
using ISEStudio.Tests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.Tests.Sources;

public sealed class SourceObjectStorageAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Endpoint_handler_bounds_declared_and_streamed_sdk_response_bytes(bool declared)
    {
        var body = new CountingBody(ObjectStorageSourceAdapter.MaxItemBytes + 1);
        using var client = EndpointClient(new Transport(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (declared) response.Content.Headers.ContentLength = body.Length;
            return response;
        }));
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
        {
            using var response = await client.GetAsync("https://storage.googleapis.com/storage/v1/b/test-bucket/o", HttpCompletionOption.ResponseHeadersRead);
            await response.Content.CopyToAsync(Stream.Null);
        });
        Assert.True(body.BytesRead <= ObjectStorageSourceAdapter.MaxItemBytes + 1);
        if (declared) Assert.Equal(0, body.BytesRead);
    }

    private static HttpClient EndpointClient(HttpMessageHandler transport)
    {
        var type = typeof(GcsSourceAdapter).Assembly.GetType("ISEStudio.Sources.Adapters.ObjectStorageEndpointHandler")!;
        var handler = (DelegatingHandler)Activator.CreateInstance(type, [new[] { "storage.googleapis.com", "oauth2.googleapis.com" }])!;
        handler.InnerHandler!.Dispose();
        handler.InnerHandler = transport;
        return new HttpClient(handler);
    }

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Exception> Failures { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return Task.FromResult(respond(request)); }
            catch (Exception exception) { Failures.Add(exception); throw; }
        }
    }

    private sealed class CountingBody(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Fill((byte)' ');
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("s3", "/report.txt")]
    [InlineData("gcs", "/report.txt")]
    [InlineData("s3", "docs/../report.txt")]
    [InlineData("gcs", "docs/./report.txt")]
    [InlineData("s3", "docs\\report.txt")]
    [InlineData("gcs", "docs\\report.txt")]
    [InlineData("s3", "..")]
    [InlineData("gcs", ".")]
    public async Task Cloud_keys_remain_opaque_while_filenames_are_safe(string kind, string key)
    {
        using var secrets = Secrets();
        var source = Source(kind, secrets);
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config)!;
        config["prefix"] = "";
        source.Config = JsonSerializer.Serialize(config);
        var sdk = new FakeSdk { ExpectedPrefix = "", List = _ => new([Object(key)], null) };
        var item = Assert.Single(await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(source, default)));
        Assert.Equal($"{(kind == "gcs" ? "gs" : "s3")}://test-bucket/{key}", item.ExternalKey);
        Assert.False(string.IsNullOrWhiteSpace(item.Filename));
        Assert.DoesNotContain('/', item.Filename);
        Assert.DoesNotContain('\\', item.Filename);
        Assert.DoesNotContain(item.Filename, new[] { ".", ".." });
    }

    [Fact]
    public void Gcs_bucket_allows_underscores_but_s3_does_not()
    {
        using var secrets = Secrets();
        foreach (var kind in new[] { "gcs", "s3" })
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, string>>(Source(kind, secrets).Config)!;
            config["bucket"] = "test_bucket";
            var error = ObjectStorageSourceConfig.Validate(kind, JsonSerializer.SerializeToElement(config));
            if (kind == "gcs") Assert.Null(error);
            else Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData("test-key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAA==\n-----END PRIVATE KEY-----")]
    public void Gcs_validation_rejects_non_pkcs8_rsa_keys(string invalidKey)
    {
        var account = JsonSerializer.Deserialize<Dictionary<string, string>>(ServiceAccountJson)!;
        account["private_key"] = invalidKey;
        Assert.Equal("GCS service account key is invalid.", ObjectStorageSourceConfig.ValidateServiceAccount(JsonSerializer.Serialize(account)));
    }

    [Theory]
    [InlineData("s3", false)]
    [InlineData("gcs", false)]
    [InlineData("s3", true)]
    [InlineData("gcs", true)]
    public async Task Real_sdk_pagination_pins_versions_and_412_preserves_successful_objects(string kind, bool conflict)
    {
        using var secrets = Secrets();
        var requests = new List<Uri>();
        var listTokens = new List<string?>();
        var conditions = new List<string>();
        var transports = new List<Transport>();
        Func<HttpMessageHandler> transport = () =>
        {
            var handler = new Transport(request =>
        {
            var uri = request.RequestUri!;
            AssertSafe(uri);
            requests.Add(uri);
            if (uri.Host == "oauth2.googleapis.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/token", uri.AbsolutePath);
                return Body("{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", "application/json");
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(request.Headers.TryGetValues("Authorization", out var authorization));
            if (kind == "s3")
            {
                Assert.Equal("s3.us-east-1.amazonaws.com", uri.Host);
                Assert.Equal("AWS4-HMAC-SHA256", authorization!.Single().Split(' ', 2)[0]);
            }
            else
            {
                Assert.Equal("storage.googleapis.com", uri.Host);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            }
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            var listing = kind == "s3" ? query.ContainsKey("list-type") : uri.AbsolutePath.EndsWith("/o", StringComparison.Ordinal);
            if (listing)
            {
                Assert.Equal("docs/", query["prefix"].ToString());
                query.TryGetValue(kind == "s3" ? "continuation-token" : "pageToken", out var tokenValue);
                var pageToken = tokenValue.ToString();
                listTokens.Add(pageToken.Length == 0 ? null : pageToken);
                var second = pageToken.Length != 0;
                if (kind == "s3")
                {
                    Assert.Equal("/test-bucket", uri.AbsolutePath.TrimEnd('/'));
                    Assert.Equal("2", query["list-type"].ToString());
                    Assert.Equal("1000", query["max-keys"].ToString());
                    return Body("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>" + (!second).ToString().ToLowerInvariant()
                        + "</IsTruncated>" + (second ? "" : "<NextContinuationToken>next +/=</NextContinuationToken>")
                        + string.Concat((second ? new[] { "c" } : new[] { "a", "b" }).Select(name =>
                            $"<Contents><Key>docs/{name}.txt</Key><Size>5</Size><ETag>&quot;{name}-etag&quot;</ETag><LastModified>2024-02-03T04:05:06Z</LastModified></Contents>"))
                        + "</ListBucketResult>", "application/xml");
                }
                Assert.Equal("/storage/v1/b/test-bucket/o", uri.AbsolutePath);
                query.TryGetValue("fields", out var fields);
                Assert.Equal("items(name,size,generation,updated),nextPageToken", fields.ToString());
                return Body(JsonSerializer.Serialize(new
                {
                    items = (second ? new[] { "c" } : new[] { "a", "b" }).Select(name => new
                    { name = $"docs/{name}.txt", size = "5", generation = (11 + name[0] - 'a').ToString(), updated = "2024-02-03T04:05:06Z" }),
                    nextPageToken = second ? null : "next +/=",
                }), "application/json");
            }
            var objectName = Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Last().Split('.')[0];
            Assert.Contains(objectName, new[] { "a", "b", "c" });
            if (kind == "s3")
            {
                Assert.Equal($"/test-bucket/docs/{objectName}.txt", uri.AbsolutePath);
                Assert.Equal($"\"{objectName}-etag\"", request.Headers.GetValues("If-Match").Single());
            }
            else
            {
                Assert.Equal($"/storage/v1/b/test-bucket/o/docs/{objectName}.txt", Uri.UnescapeDataString(uri.AbsolutePath));
                var generation = (11 + objectName[0] - 'a').ToString();
                Assert.Equal(generation, query["generation"].ToString());
                Assert.Equal(generation, query["ifGenerationMatch"].ToString());
                Assert.Equal("media", query["alt"].ToString());
            }
            conditions.Add(objectName);
            if (conflict && objectName == "b")
                return Body(kind == "s3" ? "<Error><Code>PreconditionFailed</Code><Message>SECRET upstream</Message></Error>"
                    : "{\"error\":{\"code\":412,\"message\":\"SECRET upstream\"}}", kind == "s3" ? "application/xml" : "application/json", System.Net.HttpStatusCode.PreconditionFailed);
            return Body("fresh", "text/plain");
            });
            transports.Add(handler);
            return handler;
        };
        var adapter = ProductionAdapter(kind, transport, secrets);
        var scan = await adapter.DiscoverAsync(Source(kind, secrets), default);
        var items = new List<SourceItem>();
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in scan.Items)
            {
                items.Add(item);
                await using var content = item.Content;
                using var reader = new StreamReader(content);
                Assert.Equal("fresh", await reader.ReadToEndAsync());
            }
        });
        Assert.Empty(transports.SelectMany(handler => handler.Failures));
        if (conflict) Assert.DoesNotContain("SECRET", Assert.IsType<ObjectStorageSourceException>(error).ToString());
        else Assert.Null(error);
        Assert.Equal(conflict ? 2 : 3, items.Count);
        Assert.EndsWith("/docs/c.txt", items.Last().ExternalKey);
        Assert.All(items, item => Assert.Equal(DateTimeOffset.Parse("2024-02-03T04:05:06Z"), item.DocTime));
        Assert.Equal(new string?[] { null, "next +/=" }, listTokens);
        Assert.Equal(new[] { "a", "b", "c" }, conditions);
        Assert.Equal(kind == "gcs" ? 6 : 5, requests.Count);
    }

    private static ISourceAdapter ProductionAdapter(string kind, Func<HttpMessageHandler> transport, ISourceSecretProtector secrets)
    {
        var type = kind == "s3" ? typeof(S3SourceClientFactory) : typeof(GcsSourceClientFactory);
        var constructor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(Func<HttpMessageHandler>)], null);
        Assert.NotNull(constructor);
        var factory = constructor.Invoke([transport]);
        return kind == "s3" ? new S3SourceAdapter((IS3SourceClientFactory)factory, secrets, TimeProvider.System)
            : new GcsSourceAdapter((IGcsSourceClientFactory)factory, secrets, TimeProvider.System);
    }

    private static HttpResponseMessage Body(string body, string contentType, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static void AssertSafe(Uri uri)
    {
        Assert.Equal("https", uri.Scheme);
        Assert.Equal(443, uri.Port);
        Assert.Empty(uri.UserInfo);
        Assert.Contains(uri.Host, new[] { "s3.us-east-1.amazonaws.com", "storage.googleapis.com", "oauth2.googleapis.com" });
    }

    [Fact]
    public async Task Endpoint_handler_shares_response_budget_across_requests()
    {
        using var client = EndpointClient(new Transport(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StreamContent(new CountingBody(11 * 1024 * 1024)) }));
        using var first = await client.GetAsync("https://storage.googleapis.com/first", HttpCompletionOption.ResponseHeadersRead);
        await first.Content.CopyToAsync(Stream.Null);
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
        {
            using var second = await client.GetAsync("https://storage.googleapis.com/second", HttpCompletionOption.ResponseHeadersRead);
            await second.Content.CopyToAsync(Stream.Null);
        });
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Download_sink_shares_scan_budget_and_preserves_prior_success(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk
        {
            List = _ => new([Object("docs/a.txt"), Object("docs/b.txt")], null),
            Download = async (_, output, ct) => await output.WriteAsync(new byte[11 * 1024 * 1024], ct),
        };
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default);
        await using var iterator = scan.Items.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(11 * 1024 * 1024, iterator.Current.Content.Length);
        await iterator.Current.Content.DisposeAsync();
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () => await iterator.MoveNextAsync());
    }

    [Theory]
    [InlineData("s3", "list-stream")]
    [InlineData("gcs", "list-stream")]
    [InlineData("s3", "list-length")]
    [InlineData("gcs", "list-length")]
    [InlineData("s3", "error-stream")]
    [InlineData("gcs", "error-stream")]
    [InlineData("s3", "download-stream")]
    [InlineData("gcs", "download-stream")]
    [InlineData("gcs", "auth-stream")]
    [InlineData("gcs", "auth-length")]
    [InlineData("s3", "redirect")]
    [InlineData("gcs", "redirect")]
    [InlineData("gcs", "auth-redirect")]
    [InlineData("s3", "content-type")]
    [InlineData("gcs", "content-type")]
    [InlineData("s3", "headers")]
    [InlineData("gcs", "headers")]
    public async Task Real_sdk_rejects_unsafe_responses_before_unbounded_callbacks(string kind, string defect)
    {
        using var secrets = Secrets();
        var bodies = new List<CountingBody>();
        var requests = new List<Uri>();
        Func<HttpMessageHandler> transport = () => new Transport(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            AssertSafe(uri);
            if (uri.Host == "oauth2.googleapis.com" && !defect.StartsWith("auth-", StringComparison.Ordinal))
                return Body("{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", "application/json");
            var list = kind == "s3" ? uri.Query.Contains("list-type=2", StringComparison.Ordinal) : uri.AbsolutePath.EndsWith("/o", StringComparison.Ordinal);
            if (defect == "download-stream" && list)
                return kind == "s3" ? Body("<ListBucketResult><IsTruncated>false</IsTruncated><Contents><Key>docs/a.txt</Key><Size>5</Size><ETag>&quot;a-etag&quot;</ETag><LastModified>2024-02-03T04:05:06Z</LastModified></Contents></ListBucketResult>", "application/xml")
                    : Body("{\"items\":[{\"name\":\"docs/a.txt\",\"size\":\"5\",\"generation\":\"11\",\"updated\":\"2024-02-03T04:05:06Z\"}]}", "application/json");
            if (defect.EndsWith("redirect", StringComparison.Ordinal))
            {
                var redirect = Body("", "text/plain", System.Net.HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://169.254.169.254/SECRET");
                return redirect;
            }
            if (defect is "content-type" or "headers")
            {
                var invalid = Body("{}", "application/json");
                if (defect == "content-type")
                {
                    invalid.Content.Headers.Remove("Content-Type");
                    invalid.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; malformed=\"");
                }
                else invalid.Headers.TryAddWithoutValidation("X-Large", new string('x', 65537));
                return invalid;
            }
            var body = new CountingBody(ObjectStorageSourceAdapter.MaxItemBytes + 1);
            bodies.Add(body);
            var response = new HttpResponseMessage(defect == "error-stream" ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.OK)
                { Content = new StreamContent(body) };
            if (defect.EndsWith("length", StringComparison.Ordinal)) response.Content.Headers.ContentLength = body.Length;
            return response;
        });
        var count = 0;
        var error = await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
        {
            var scan = await ProductionAdapter(kind, transport, secrets).DiscoverAsync(Source(kind, secrets), default);
            await foreach (var item in scan.Items)
            {
                count++;
                await item.Content.DisposeAsync();
            }
        });
        Assert.Equal(0, count);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.NotEmpty(requests);
        Assert.All(requests, AssertSafe);
        Assert.All(bodies, body => Assert.True(body.BytesRead <= ObjectStorageSourceAdapter.MaxItemBytes + 1));
        if (defect.EndsWith("length", StringComparison.Ordinal)) Assert.All(bodies, body => Assert.Equal(0, body.BytesRead));
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://169.254.169.254/")]
    [InlineData("https://storage.googleapis.com.evil.test/")]
    [InlineData("http://storage.googleapis.com/")]
    [InlineData("https://storage.googleapis.com:444/")]
    [InlineData("https://user:SECRET@storage.googleapis.com/")]
    public async Task Endpoint_handler_blocks_untrusted_targets_before_transport(string target)
    {
        var calls = 0;
        using var client = EndpointClient(new Transport(_ => { calls++; return Body("", "text/plain"); }));
        var error = await Assert.ThrowsAsync<ObjectStorageSourceException>(() => client.GetAsync(target));
        Assert.Equal(0, calls);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData("not-a-length")]
    [InlineData("-1")]
    [InlineData("5, 6")]
    public async Task Endpoint_handler_rejects_malformed_content_length_before_reading(string length)
    {
        var body = new CountingBody(5);
        using var client = EndpointClient(new Transport(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Length", length);
            return response;
        }));
        await Assert.ThrowsAsync<ObjectStorageSourceException>(() => client.GetAsync("https://storage.googleapis.com/", HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task Endpoint_handler_allows_exactly_twenty_mebibytes_without_truncation()
    {
        var body = new CountingBody(ObjectStorageSourceAdapter.MaxItemBytes);
        using var client = EndpointClient(new Transport(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var response = await client.GetAsync("https://storage.googleapis.com/", HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
        Assert.Equal(ObjectStorageSourceAdapter.MaxItemBytes, body.BytesRead);
    }

    [Theory]
    [InlineData("s3", "/docs/../report.txt")]
    [InlineData("gcs", "/docs/../report.txt")]
    [InlineData("s3", "docs\\report.txt")]
    [InlineData("gcs", "docs\\report.txt")]
    [InlineData("s3", "docs/./report.txt")]
    [InlineData("gcs", "docs/./report.txt")]
    public async Task Real_sdk_downloads_opaque_names_without_path_normalization(string kind, string key)
    {
        using var secrets = Secrets();
        var source = Source(kind, secrets);
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config)!;
        config["prefix"] = "";
        source.Config = JsonSerializer.Serialize(config);
        var downloads = new List<string>();
        Func<HttpMessageHandler> transport = () => new Transport(request =>
        {
            var uri = request.RequestUri!;
            AssertSafe(uri);
            if (uri.Host == "oauth2.googleapis.com")
                return Body("{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", "application/json");
            var list = kind == "s3" ? uri.Query.Contains("list-type=2", StringComparison.Ordinal) : uri.AbsolutePath.EndsWith("/o", StringComparison.Ordinal);
            if (list)
                return kind == "s3" ? Body($"<ListBucketResult><IsTruncated>false</IsTruncated><Contents><Key>{System.Security.SecurityElement.Escape(key)}</Key><Size>5</Size><ETag>&quot;etag&quot;</ETag><LastModified>2024-02-03T04:05:06Z</LastModified></Contents></ListBucketResult>", "application/xml")
                    : Body(JsonSerializer.Serialize(new { items = new[] { new { name = key, size = "5", generation = "11", updated = "2024-02-03T04:05:06Z" } } }), "application/json");
            var pathStart = uri.OriginalString.IndexOf('/', uri.OriginalString.IndexOf("://", StringComparison.Ordinal) + 3);
            downloads.Add(Uri.UnescapeDataString(uri.OriginalString[pathStart..].Split('?')[0]));
            return Body("fresh", "text/plain");
        });
        var item = Assert.Single(await Collect(await ProductionAdapter(kind, transport, secrets).DiscoverAsync(source, default)));
        Assert.Equal($"{(kind == "s3" ? "s3" : "gs")}://test-bucket/{key}", item.ExternalKey);
        Assert.Equal((kind == "s3" ? "/test-bucket/" : "/storage/v1/b/test-bucket/o/") + key, Assert.Single(downloads));
        Assert.Equal("report.txt", item.Filename);
    }

    [Fact]
    public async Task Official_gcs_client_uses_storage_api_path_and_releases_auth_http_client_and_key()
    {
        using var key = RSA.Create(2048);
        var account = JsonSerializer.Deserialize<Dictionary<string, string>>(ServiceAccountJson)!;
        account["private_key"] = key.ExportPkcs8PrivateKeyPem();
        using var sdk = new GcsSourceClientFactory().Create(new(JsonSerializer.Serialize(account)));
        var field = sdk.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(item => item.FieldType == typeof(StorageClient));
        var client = Assert.IsAssignableFrom<StorageClient>(field.GetValue(sdk));
        Assert.Equal("https://storage.googleapis.com/storage/v1/", client.Service.BaseUri);
        var credential = Assert.IsType<GoogleCredential>(client.Service.HttpClientInitializer);
        var auth = Assert.IsType<ServiceAccountCredential>(credential.UnderlyingCredential);
        Assert.Equal("https://oauth2.googleapis.com/token", auth.TokenServerUrl);
        Assert.Equal("googleapis.com", auth.UniverseDomain);
        sdk.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => auth.HttpClient.GetAsync("https://oauth2.googleapis.com/token", cancelled.Token));
        Assert.Throws<ObjectDisposedException>(() => auth.Key.ExportParameters(true));
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Storage_api_seals_all_secrets_preserves_on_patch_and_never_projects_credentials(string kind)
    {
        using var app = new AuthTestWebApplicationFactory(null, Convert.ToBase64String(new byte[32]));
        await app.SeedAdminAsync();
        using var client = app.CreateClient();
        await app.AuthenticateAsAsync(client);
        var knowledgeResponse = await client.PostAsJsonAsync("/api/knowledge", new { name = $"task4-{Guid.NewGuid():N}" });
        knowledgeResponse.EnsureSuccessStatusCode();
        var ks = (await knowledgeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var route = $"/api/knowledge/{ks}/ingestion-sources";
        var kinds = await client.GetFromJsonAsync<JsonElement>(route + "/kinds");
        var descriptor = Assert.Single(kinds.EnumerateArray(), item => item.GetProperty("kind").GetString() == kind);
        Assert.True(descriptor.GetProperty("active_sync").GetBoolean());
        var config = new Dictionary<string, string> { ["bucket"] = "test-bucket", ["prefix"] = "docs/" };
        if (kind == "s3")
        {
            config["region"] = "us-east-1";
            config["access_key_id"] = "test-access";
            config["secret_access_key"] = "test-secret";
            config["session_token"] = "test-session";
        }
        else config["service_account_key"] = ServiceAccountJson;
        var created = await client.PostAsJsonAsync(route, new { kind, name = "Storage", config });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var db = app.CreateDbContext();
        var source = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == id);
        using var stored = JsonDocument.Parse(source.Config);
        var protector = app.Services.GetRequiredService<ISourceSecretProtector>();
        var fields = ObjectStorageSourceConfig.Descriptors.Single(item => item.Kind == kind).ConfigFields.Where(field => field.Secret).ToArray();
        foreach (var field in fields)
        {
            var sealedValue = stored.RootElement.GetProperty(field.Name).GetString()!;
            Assert.StartsWith("v1:", sealedValue);
            Assert.Equal(config[field.Name], protector.Open(sealedValue, $"{ks:D}:{id:D}:{field.Name}"));
        }
        var patched = await client.PatchAsJsonAsync(route + $"/{id}", new { kind, name = "Storage", config = new { prefix = "new/" } });
        patched.EnsureSuccessStatusCode();
        var persisted = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == id);
        using var updated = JsonDocument.Parse(persisted.Config);
        Assert.Equal("new/", updated.RootElement.GetProperty("prefix").GetString());
        foreach (var field in fields)
            Assert.Equal(stored.RootElement.GetProperty(field.Name).GetString(), updated.RootElement.GetProperty(field.Name).GetString());
        foreach (var response in new[] { await created.Content.ReadAsStringAsync(), await patched.Content.ReadAsStringAsync(),
            await client.GetStringAsync(route), await client.GetStringAsync(route + $"/{id}") })
        {
            foreach (var field in fields) Assert.DoesNotContain(config[field.Name], response);
            Assert.DoesNotContain("v1:", response);
        }
        var detail = await client.GetFromJsonAsync<JsonElement>(route + $"/{id}");
        Assert.Equal("new/", detail.GetProperty("config").GetProperty("prefix").GetString());
        foreach (var field in fields) Assert.False(detail.GetProperty("config").TryGetProperty(field.Name, out _));
        var audits = await db.AuditEvents.AsNoTracking().Where(item => item.KnowledgeSystemId == ks).ToListAsync();
        foreach (var audit in audits)
            foreach (var field in fields) Assert.DoesNotContain(config[field.Name], audit.Detail?.RootElement.GetRawText() ?? string.Empty);

        var invalidPatch = await client.PatchAsJsonAsync(route + $"/{id}", new { kind, name = "Storage", config = new { endpoint = "http://127.0.0.1/SECRET" } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidPatch.StatusCode);
        if (kind == "gcs")
        {
            var malicious = ServiceAccountJson.Replace("https://oauth2.googleapis.com/token", "http://169.254.169.254/SECRET", StringComparison.Ordinal);
            var rejected = await client.PatchAsJsonAsync(route + $"/{id}", new { kind, name = "Storage", config = new { service_account_key = malicious } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.DoesNotContain("SECRET", await rejected.Content.ReadAsStringAsync());
            var invalidAccount = JsonSerializer.Deserialize<Dictionary<string, string>>(ServiceAccountJson)!;
            invalidAccount["private_key"] = "test-key";
            var invalidKey = JsonSerializer.Serialize(invalidAccount);
            var invalidCreate = await client.PostAsJsonAsync(route, new { kind, name = "Invalid key", config = new { bucket = "test_bucket", service_account_key = invalidKey } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidCreate.StatusCode);
            var invalidKeyPatch = await client.PatchAsJsonAsync(route + $"/{id}", new { kind, name = "Storage", config = new { service_account_key = invalidKey } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidKeyPatch.StatusCode);
            Assert.DoesNotContain("test-key", await invalidKeyPatch.Content.ReadAsStringAsync());
            var afterRejection = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == id);
            using var stillSealed = JsonDocument.Parse(afterRejection.Config);
            Assert.True(stored.RootElement.GetProperty("service_account_key").GetString() == stillSealed.RootElement.GetProperty("service_account_key").GetString());
        }
        var cleared = await client.PatchAsJsonAsync(route + $"/{id}", new { kind, name = "Storage", config = fields.ToDictionary(field => field.Name, _ => (string?)null) });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, cleared.StatusCode);
    }

    [Theory]
    [InlineData("s3", "bucket", "127.0.0.1")]
    [InlineData("gcs", "bucket", "evil..bucket")]
    [InlineData("s3", "bucket", "bucket--x-s3")]
    [InlineData("s3", "prefix", "docs/\u0000escape")]
    [InlineData("gcs", "prefix", "docs/\u0001escape")]
    [InlineData("s3", "region", "https://evil.test")]
    [InlineData("s3", "region", "aws-global")]
    [InlineData("gcs", "region", "us-east-1")]
    [InlineData("gcs", "endpoint", "https://evil.test")]
    public void Config_rejects_unsafe_bucket_prefix_endpoint_and_nonregional_s3(string kind, string field, string value)
    {
        using var secrets = Secrets();
        var source = Source(kind, secrets);
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config)!;
        config[field] = value;
        Assert.NotNull(ObjectStorageSourceConfig.Validate(kind, JsonSerializer.SerializeToElement(config)));
    }

    [Theory]
    [InlineData("token_uri", "http://169.254.169.254/")]
    [InlineData("token_uri", "https://oauth2.googleapis.com.evil.test/token")]
    [InlineData("token_uri", "https://oauth2.googleapis.com/token?secret=x")]
    [InlineData("universe_domain", "evil.test")]
    [InlineData("type", "external_account")]
    public async Task Gcs_malicious_service_account_fails_before_any_sdk_call(string field, string value)
    {
        using var secrets = Secrets();
        var source = Source("gcs", secrets);
        var json = JsonSerializer.Deserialize<Dictionary<string, string>>(ServiceAccountJson)!;
        json[field] = value;
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config)!;
        config["service_account_key"] = secrets.Seal(JsonSerializer.Serialize(json), $"{source.KnowledgeSystemId:D}:{source.Id:D}:service_account_key");
        source.Config = JsonSerializer.Serialize(config);
        var sdk = new FakeSdk();
        await Assert.ThrowsAsync<ObjectStorageSourceException>(() => Adapter("gcs", sdk, secrets).DiscoverAsync(source, default));
        Assert.Equal(0, sdk.Creates);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Thirty_second_deadline_is_shared_across_pages_and_prevents_further_downloads(string kind)
    {
        using var secrets = Secrets();
        var clock = new DeadlineClock();
        var sdk = new FakeSdk { List = token =>
        {
            if (token is not null) clock.Expire();
            return new([Object("docs/a.txt")], token is null ? "next" : null);
        } };
        ISourceAdapter adapter = kind == "s3" ? new S3SourceAdapter(sdk, secrets, clock) : new GcsSourceAdapter(sdk, secrets, clock);
        var scan = await adapter.DiscoverAsync(Source(kind, secrets), default);
        await using var iterator = scan.Items.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        await iterator.Current.Content.DisposeAsync();
        var error = await Assert.ThrowsAsync<ObjectStorageSourceException>(async () => await iterator.MoveNextAsync());
        Assert.Contains("timed out", error.Message);
        Assert.Single(sdk.Versions);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.DueTime);
        Assert.Equal(1, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task External_identity_must_fit_persistent_column_including_bucket(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk { List = _ => new([Object("docs/" + new string('a', 1019))], null) };
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
            await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default)));
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; DueTime = dueTime; return new InertTimer(); }
        public void Expire() => _callback!(_state);
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("s3", "s3")]
    [InlineData("gcs", "gs")]
    public async Task Pages_preserve_bucket_key_identity_timestamp_and_download_fresh_versions(string kind, string scheme)
    {
        using var secrets = Secrets();
        var source = Source(kind, secrets);
        var time = DateTimeOffset.Parse("2024-02-03T04:05:06Z");
        var sdk = new FakeSdk
        {
            List = token => token is null
                ? new([new("docs/a/report.txt", 5, "version-1", time)], "page-2")
                : new([new("docs/b/report.txt", 5, "version-2", time)], null),
        };
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(source, default);
        Assert.True(scan.IsComplete);
        var items = await Collect(scan);
        Assert.Equal([$"{scheme}://test-bucket/docs/a/report.txt", $"{scheme}://test-bucket/docs/b/report.txt"],
            items.Select(item => item.ExternalKey));
        Assert.All(items, item => Assert.Equal(time, item.DocTime));
        Assert.All(items, item => Assert.Equal("report.txt", item.Filename));
        Assert.Equal(new string?[] { null, "page-2" }, sdk.Tokens);
        Assert.Equal(["version-1", "version-2"], sdk.Versions);
        sdk.List = _ => new([new("docs/a/report.txt", 5, "version-3", time)], null);
        var changed = await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(source, default));
        Assert.Equal(items[0].ExternalKey, Assert.Single(changed).ExternalKey);
        Assert.Equal("version-3", sdk.Versions.Last());
        Assert.Equal(2, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Interrupted_page_yields_prior_items_then_throws_sanitized_error(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk { List = token => token is null ? new([Object("docs/a.txt")], "next") : throw new IOException("SECRET upstream URL") };
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default);
        await using var iterator = scan.Items.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        await iterator.Current.Content.DisposeAsync();
        var error = await Assert.ThrowsAsync<ObjectStorageSourceException>(async () => await iterator.MoveNextAsync());
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Equal(1, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Download_failure_preserves_items_before_and_after_failure_and_never_completes(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk
        {
            List = _ => new([Object("docs/a.txt"), Object("docs/b.txt"), Object("docs/c.txt")], null),
            Download = async (item, output, ct) =>
            {
                if (item.Key == "docs/b.txt") throw new IOException("SECRET download response");
                await output.WriteAsync("fresh"u8.ToArray(), ct);
            },
        };
        var keys = new List<string>();
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default);
        var error = await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                keys.Add(item.ExternalKey);
                await item.Content.DisposeAsync();
            }
        });
        Assert.Equal(2, keys.Count);
        Assert.EndsWith("/docs/c.txt", keys[1]);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Equal(1, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3", "duplicate")]
    [InlineData("gcs", "duplicate")]
    [InlineData("s3", "cycle")]
    [InlineData("gcs", "cycle")]
    [InlineData("s3", "scope")]
    [InlineData("gcs", "scope")]
    public async Task Invalid_listing_fails_without_silent_completion(string kind, string defect)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk { List = _ => defect switch
        {
            "duplicate" => new([Object("docs/a.txt"), Object("docs/a.txt")], null),
            "scope" => new([Object("outside.txt")], null),
            _ => new([], "cycle"),
        } };
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
            await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default)));
        Assert.Equal(1, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3", false)]
    [InlineData("gcs", false)]
    [InlineData("s3", true)]
    [InlineData("gcs", true)]
    public async Task Item_limit_counts_across_pages_and_fails_even_if_empty_pages_continue(string kind, bool emptyPages)
    {
        using var secrets = Secrets();
        var page = 0;
        var sdk = new FakeSdk { List = _ => new(emptyPages ? [] : [Object($"docs/{page}.txt")], (++page).ToString()) };
        var yielded = 0;
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default);
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
        {
            await foreach (var item in scan.Items)
            {
                yielded++;
                await item.Content.DisposeAsync();
            }
        });
        Assert.Equal(emptyPages ? 0 : 1000, yielded);
        Assert.Equal(1000, sdk.Tokens.Count);
    }

    [Theory]
    [InlineData("s3", false)]
    [InlineData("gcs", false)]
    [InlineData("s3", true)]
    [InlineData("gcs", true)]
    public async Task Byte_limit_checks_metadata_and_actual_download(string kind, bool oversizedMetadata)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk
        {
            List = _ => new([Object("docs/large.txt") with { Size = oversizedMetadata ? 20 * 1024 * 1024 + 1 : 1 }], null),
            Download = async (_, output, ct) => await output.WriteAsync(new byte[20 * 1024 * 1024 + 1], ct),
        };
        await Assert.ThrowsAsync<ObjectStorageSourceException>(async () =>
            await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default)));
        Assert.Equal(oversizedMetadata ? 0 : 1, sdk.Versions.Count);
        Assert.Equal(1, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Empty_bucket_completes_and_early_disposal_closes_sdk(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk { List = _ => new([], null) };
        Assert.Empty(await Collect(await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default)));
        sdk.List = _ => new([Object("docs/a.txt"), Object("docs/b.txt")], null);
        var scan = await Adapter(kind, sdk, secrets).DiscoverAsync(Source(kind, secrets), default);
        await using (var iterator = scan.Items.GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            await iterator.Current.Content.DisposeAsync();
        }
        Assert.Equal(2, sdk.Disposals);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Missing_or_unsealed_credentials_fail_before_creating_sdk(string kind)
    {
        using var secrets = Secrets();
        var sdk = new FakeSdk();
        var source = Source(kind, secrets);
        source.Config = kind == "s3"
            ? "{\"bucket\":\"test-bucket\",\"prefix\":\"docs/\",\"region\":\"us-east-1\"}"
            : "{\"bucket\":\"test-bucket\",\"prefix\":\"docs/\",\"service_account_key\":\"plaintext-secret\"}";
        await Assert.ThrowsAsync<ObjectStorageSourceException>(() => Adapter(kind, sdk, secrets).DiscoverAsync(source, default));
        Assert.Equal(0, sdk.Creates);
    }

    private static ObjectStorageObject Object(string key) => new(key, 5, "1", DateTimeOffset.Parse("2024-02-03T04:05:06Z"));

    private static SourceSecretProtector Secrets() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());

    private static SourceEntity Source(string kind, ISourceSecretProtector secrets)
    {
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = Guid.NewGuid(), Kind = kind };
        var config = new Dictionary<string, string> { ["bucket"] = "test-bucket", ["prefix"] = "docs/" };
        if (kind == "s3")
        {
            config["region"] = "us-east-1";
            config["access_key_id"] = "test-access";
            config["secret_access_key"] = "test-secret";
            config["session_token"] = "test-session";
        }
        else config["service_account_key"] = ServiceAccountJson;
        foreach (var field in ObjectStorageSourceConfig.Descriptors.Single(item => item.Kind == kind).ConfigFields.Where(field => field.Secret))
            if (config.TryGetValue(field.Name, out var value))
                config[field.Name] = secrets.Seal(value, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field.Name}");
        source.Config = JsonSerializer.Serialize(config);
        return source;
    }

    private static readonly string ServiceAccountJson = TestServiceAccount();

    private static string TestServiceAccount()
    {
        using var key = RSA.Create(2048);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account", ["client_email"] = "source@test.iam.gserviceaccount.com",
            ["private_key"] = key.ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.googleapis.com/token",
            ["universe_domain"] = "googleapis.com",
        });
    }

    private static ISourceAdapter Adapter(string kind, FakeSdk sdk, ISourceSecretProtector secrets) => kind == "s3"
        ? new S3SourceAdapter(sdk, secrets, TimeProvider.System)
        : new GcsSourceAdapter(sdk, secrets, TimeProvider.System);

    private static async Task<List<SourceItem>> Collect(SourceScan scan)
    {
        var items = new List<SourceItem>();
        await foreach (var item in scan.Items)
        {
            items.Add(item);
            await using var stream = item.Content;
            using var reader = new StreamReader(stream);
            Assert.Equal("fresh", await reader.ReadToEndAsync());
        }
        return items;
    }

    private sealed class FakeSdk : IS3SourceClientFactory, IGcsSourceClientFactory, IObjectStorageSourceClient
    {
        public Func<string?, ObjectStoragePage> List { get; set; } = _ => new([], null);
        public string ExpectedPrefix { get; set; } = "docs/";
        public Func<ObjectStorageObject, Stream, CancellationToken, Task>? Download { get; set; }
        public List<string?> Tokens { get; } = [];
        public List<string?> Versions { get; } = [];
        public int Creates { get; private set; }
        public int Disposals { get; private set; }
        public IObjectStorageSourceClient Create(S3SourceCredentials credentials) { Creates++; Assert.Equal("test-session", credentials.SessionToken); return this; }
        public IObjectStorageSourceClient Create(GcsSourceCredentials credentials) { Creates++; Assert.Equal(ServiceAccountJson, credentials.ServiceAccountKey); return this; }
        public Task<ObjectStoragePage> ListAsync(string bucket, string prefix, string? token, CancellationToken ct)
        {
            Assert.Equal("test-bucket", bucket);
            Assert.Equal(ExpectedPrefix, prefix);
            ct.ThrowIfCancellationRequested();
            Tokens.Add(token);
            return Task.FromResult(List(token));
        }
        public async Task<string?> DownloadAsync(string bucket, ObjectStorageObject item, Stream destination, CancellationToken ct)
        {
            Versions.Add(item.Version);
            if (Download is not null) await Download(item, destination, ct);
            else await destination.WriteAsync(Encoding.UTF8.GetBytes("fresh"), ct);
            return "text/plain";
        }
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public void Ready_storage_kinds_are_active_and_do_not_offer_endpoint_overrides(string kind)
    {
        using var services = new ServiceCollection().AddSourceServices().BuildServiceProvider();
        var registry = services.GetRequiredService<SourceAdapterRegistry>();
        Assert.True(registry.TryGet(kind, out var descriptor));
        Assert.True(descriptor.ActiveSync);
        Assert.DoesNotContain(descriptor.ConfigFields, field => field.Name == "endpoint");
        Assert.False(registry.TryGet("azure_blob", out _));
        Assert.False(registry.TryGet("memory", out _));
        Assert.False(registry.TryGet("upload", out _));
    }
}