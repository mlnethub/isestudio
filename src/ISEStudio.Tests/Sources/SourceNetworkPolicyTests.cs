using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;
using ISEStudio.Sources.Networking;
using ISEStudio.Tests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Sources;

public sealed class SourceNetworkPolicyTests
{
    [Theory]
    [InlineData("GET", 206, false)]
    [InlineData("GET", 200, true)]
    [InlineData("POST", 206, false)]
    [InlineData("POST", 200, true)]
    [InlineData("PROPFIND", 206, false)]
    [InlineData("PROPFIND", 207, true)]
    public async Task Full_body_requests_reject_partial_status_or_ContentRange_before_reading(string method, int status, bool contentRange)
    {
        var reads = 0;
        var transport = new FakeTransport(_ =>
        {
            var message = Body(new ProbeStream(() => reads++));
            message.StatusCode = (HttpStatusCode)status;
            if (contentRange) message.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-0/100");
            return message;
        });
        var client = Client(transport);
        await Assert.ThrowsAsync<SourceNetworkException>(() => method switch
        {
            "POST" => Post(client, "{}"u8.ToArray()),
            "PROPFIND" => Send(client, propfind: true),
            _ => Send(client),
        });
        Assert.Equal(0, reads);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task Complete_207_PROPFIND_remains_supported()
    {
        var transport = new FakeTransport(_ =>
        {
            var message = Ok("application/xml");
            message.StatusCode = HttpStatusCode.MultiStatus;
            return message;
        });
        await using var response = await Send(Client(transport), propfind: true);
        Assert.Equal(2, response.Length);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PROPFIND")]
    [InlineData("POST")]
    public async Task Response_metadata_keeps_final_URI_Link_and_type_without_exposing_other_headers(string method)
    {
        var calls = 0;
        const string link = "<?page=2>; rel=\"next\"";
        var transport = new FakeTransport(_ =>
        {
            if (calls++ == 0) return Redirect(307, "/final");
            var message = Ok();
            message.Headers.TryAddWithoutValidation("Link", link);
            message.Headers.TryAddWithoutValidation("Set-Cookie", "hidden-cookie");
            message.Headers.TryAddWithoutValidation("X-Secret", "hidden-token");
            return message;
        });
        var client = Client(transport);
        await using var response = method switch
        {
            "POST" => await client.PostJsonAsync(new Uri("https://public.test/start"), "{}"u8.ToArray(), new(), default),
            "PROPFIND" => await client.PropFindAsync(new Uri("https://public.test/start"), 1, new(), default),
            _ => await client.GetAsync(new Uri("https://public.test/start"), new(), default),
        };
        var metadata = Assert.IsType<SourceContentStream>(response);
        Assert.Equal(new Uri("https://public.test/final"), metadata.FinalUri);
        Assert.Equal(link, metadata.LinkHeader);
        Assert.Equal("application/json", metadata.ContentType);
        Assert.DoesNotContain(metadata.GetType().GetProperties(), property => property.Name is "Headers" or "ResponseHeaders" or "Message");
        Assert.Equal(2, transport.Disposals);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PROPFIND")]
    [InlineData("POST")]
    public async Task All_outgoing_requests_and_redirects_use_server_controlled_user_agent(string method)
    {
        var calls = 0;
        var transport = new FakeTransport(_ => calls++ == 0 ? Redirect(307, "/next") : Ok());
        var client = Client(transport);
        var uri = new Uri("https://public.test/");
        await using var response = method switch
        {
            "POST" => await client.PostJsonAsync(uri, "{}"u8.ToArray(), new(), default),
            "PROPFIND" => await client.PropFindAsync(uri, 1, new(), default),
            _ => await client.GetAsync(uri, new(), default),
        };
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Equal("ISEStudio-source-connectors/1.0", request.Headers.GetValueOrDefault("User-Agent")));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://public.test/file")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://192.0.2.1/")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://198.51.100.1/")]
    [InlineData("http://203.0.113.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://240.0.0.1/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[2001:db8::1]/")]
    [InlineData("http://[::ffff:8.8.8.8]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    [InlineData("http://[2001::1]/")]
    [InlineData("http://[2d00::1]/")]
    [InlineData("http://[3000::1]/")]
    [InlineData("http://[3ffe::1]/")]
    [InlineData("http://[2000::1]/")]
    [InlineData("http://[3fff::1]/")]
    [InlineData("https://user:password@public.test/")]
    public async Task Unsafe_targets_are_rejected(string target)
        => await Assert.ThrowsAsync<SourceNetworkException>(() => Policy().ValidateAsync(new Uri(target), default));

    [Theory]
    [InlineData("http://public.test/")]
    [InlineData("https://public.test/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    [InlineData("https://[2001:4860:4860::8888]/")]
    [InlineData("https://[2400:3200::1]/")]
    [InlineData("https://[2a00:1450::1]/")]
    [InlineData("https://[2c00::1]/")]
    [InlineData("https://[2410::1]/")]
    [InlineData("https://[2610::1]/")]
    [InlineData("https://[2620::1]/")]
    [InlineData("https://[2630::1]/")]
    [InlineData("https://[2a10::1]/")]
    public async Task Public_targets_are_allowed(string target)
        => Assert.NotEmpty(await Policy().ValidateAsync(new Uri(target), default));

    [Theory]
    [InlineData("2410::1")]
    [InlineData("2610::1")]
    [InlineData("2620::1")]
    [InlineData("2630::1")]
    [InlineData("2a10::1")]
    public async Task Allocated_IPv6_mixed_with_public_IPv4_is_allowed_but_private_answers_block_transport(string address)
    {
        var transport = new FakeTransport(_ => Ok());
        var answers = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address) };
        await using var response = await Client(transport, new FakeDns(_ => answers))
            .GetAsync(new Uri("https://mixed.test/"), new(), default);
        Assert.Equal(answers, Assert.Single(transport.Addresses));
        var blockedTransport = new FakeTransport(_ => Ok());
        await Assert.ThrowsAsync<SourceNetworkException>(() => Client(blockedTransport,
            new FakeDns(_ => answers.Append(IPAddress.Parse("10.0.0.1")).ToArray()))
            .GetAsync(new Uri("https://mixed.test/"), new(), default));
        Assert.Empty(blockedTransport.Requests);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("2001::1")]
    [InlineData("2002::1")]
    [InlineData("2d00::1")]
    [InlineData("3000::1")]
    [InlineData("3ffe::1")]
    [InlineData("2000::1")]
    public async Task Every_DNS_answer_must_pass_before_transport(string blocked)
    {
        var transport = new FakeTransport(_ => Ok());
        await Assert.ThrowsAsync<SourceNetworkException>(() => Client(transport,
            new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(blocked) }))
            .GetAsync(new Uri("https://mixed.test/"), new(), default));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Explicit_CIDRs_allow_reserved_IPv6_only_inside_the_configured_range()
    {
        var policy = Policy(options: new() { AllowedCidrs = new[] { "2d00::/64", "2002::/64" } });
        Assert.Single(await policy.ValidateAsync(new Uri("https://[2d00::1]/"), default));
        Assert.Single(await policy.ValidateAsync(new Uri("https://[2002::1]/"), default));
        await Assert.ThrowsAsync<SourceNetworkException>(() => policy.ValidateAsync(new Uri("https://[2d00:0:0:1::1]/"), default));
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("2606:4700::1"), IPAddress.Parse("2d00::1") });
        Assert.Equal(2, (await Policy(dns, new() { AllowedCidrs = new[] { "2d00::/64" } })
            .ValidateAsync(new Uri("https://mixed.test/"), default)).Count);
    }

    [Fact]
    public async Task Explicit_CIDRs_allow_only_matching_private_and_mapped_addresses()
    {
        var policy = Policy(options: new() { AllowedCidrs = new[] { "10.0.0.0/24", "fc00::/64", "::ffff:8.8.8.8/128" } });
        foreach (var target in new[] { "http://10.0.0.12/", "http://[fc00::12]/", "http://[::ffff:8.8.8.8]/" })
            Assert.Single(await policy.ValidateAsync(new Uri(target), default));
        foreach (var target in new[] { "http://10.0.1.12/", "http://[::ffff:127.0.0.1]/" })
            await Assert.ThrowsAsync<SourceNetworkException>(() => policy.ValidateAsync(new Uri(target), default));
    }

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("fc00::/129")]
    [InlineData("invalid")]
    public void Invalid_CIDRs_fail_closed(string cidr)
        => Assert.Throws<SourceNetworkException>(() => Policy(options: new() { AllowedCidrs = new[] { cidr } }));

    [Fact]
    public async Task Empty_DNS_and_DNS_errors_are_sanitized()
    {
        await Assert.ThrowsAsync<SourceNetworkException>(() => Policy(new FakeDns(_ => Array.Empty<IPAddress>())).ValidateAsync(new Uri("https://public.test/"), default));
        var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Policy(new FakeDns(_ => throw new SocketException()))
            .ValidateAsync(new Uri("https://public.test/?token=do-not-leak"), default));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("do-not-leak", failure.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redirects_to_private_addresses_never_reach_transport(bool propfind)
    {
        var transport = new FakeTransport(_ => Redirect(propfind ? 307 : 302, "http://127.0.0.1/"));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport), propfind));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Each_redirect_resolves_again_and_rebinding_is_rejected()
    {
        var calls = 0;
        var dns = new FakeDns(_ => new[] { IPAddress.Parse(++calls == 1 ? "8.8.8.8" : "10.0.0.1") });
        var transport = new FakeTransport(_ => Redirect(302, "/next"));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport, dns)));
        Assert.Equal(2, calls);
        Assert.Single(transport.Requests);
        Assert.Equal("8.8.8.8", Assert.Single(transport.Addresses[0]).ToString());
    }

    [Fact]
    public async Task Public_redirects_pin_the_fresh_DNS_snapshot_for_each_hop()
    {
        var calls = 0;
        var dns = new FakeDns(_ => new[] { IPAddress.Parse(++calls == 1 ? "8.8.8.8" : "1.1.1.1") });
        var transportCalls = 0;
        var transport = new FakeTransport(_ => transportCalls++ == 0 ? Redirect(302, "/next") : Ok());
        await using var result = await Send(Client(transport, dns));
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" },
            transport.Addresses.Select(addresses => Assert.Single(addresses).ToString()).ToArray());
    }

    [Fact]
    public async Task Handler_uses_a_snapshot_not_a_mutable_DNS_answer_list()
    {
        var sockets = new FakeSocketConnector();
        var addresses = new[] { IPAddress.Parse("8.8.8.8") };
        using var handler = new SourceHttpTransport(sockets).CreateHandler(new Uri("http://public.test/"), addresses);
        addresses[0] = IPAddress.Loopback;
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://public.test/");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("8.8.8.8", Assert.Single(sockets.Targets).Address.ToString());
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task Redirect_budget_is_exactly_five_hops(int redirects, bool succeeds)
    {
        var calls = 0;
        var transport = new FakeTransport(_ => calls++ < redirects ? Redirect(302, "/next") : Ok());
        if (succeeds) { await using var result = await Send(Client(transport)); }
        else await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport)));
        Assert.Equal(6, transport.Requests.Count);
        Assert.Equal(6, transport.Disposals);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Forwarded")]
    [InlineData("Depth")]
    [InlineData("X-Api-Key")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Content-Type")]
    [InlineData("User-Agent")]
    public void Ordinary_headers_are_allowlisted(string header)
        => Assert.Throws<SourceNetworkException>(() => new SourceRequestOptions(new Dictionary<string, string> { [header] = "secret" }));

    [Fact]
    public void Header_values_reject_CRLF_without_disclosing_values()
    {
        var failure = Assert.Throws<SourceNetworkException>(() => new SourceRequestOptions(new Dictionary<string, string> { ["Accept"] = "secret\r\nHost: internal" }));
        Assert.DoesNotContain("secret", failure.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_origin_https_redirect_keeps_sealed_credentials_and_allowed_headers(bool propfind)
    {
        var calls = 0;
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8") });
        var transport = new FakeTransport(_ => calls++ == 0 ? Redirect(propfind ? 307 : 302, "https://PUBLIC.test:443/next") : Ok());
        await using var result = await Send(Client(transport, dns), propfind, Credentials());
        Assert.Equal(2, dns.Calls);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("Bearer hidden-token", request.Headers["Authorization"]);
            Assert.Equal("session=hidden-cookie", request.Headers["Cookie"]);
            Assert.Equal("application/json", request.Headers["Accept"]);
            Assert.Equal("\"version\"", request.Headers["If-None-Match"]);
            Assert.Equal("2022-06-28", request.Headers["Notion-Version"]);
            Assert.Equal(propfind ? "PROPFIND" : "GET", request.Method);
            if (propfind) Assert.Equal("1", request.Headers["Depth"]);
            else Assert.False(request.Headers.ContainsKey("Depth"));
        });
    }

    [Theory]
    [InlineData("http://public.test/next")]
    [InlineData("https://public.test:444/next")]
    [InlineData("https://other.test/next")]
    public async Task Credentialed_redirects_fail_closed_before_cross_origin_transport(string target)
    {
        foreach (var propfind in new[] { false, true })
        {
            var transport = new FakeTransport(_ => Redirect(propfind ? 308 : 302, target));
            await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport), propfind, Credentials()));
            Assert.Single(transport.Requests);
        }
    }

    [Fact]
    public async Task Credentials_are_not_sent_on_initial_plain_http()
    {
        var transport = new FakeTransport(_ => Ok());
        await Assert.ThrowsAsync<SourceNetworkException>(() => Client(transport).GetAsync(new Uri("http://public.test/"), Credentials(), default));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Ordinary_headers_follow_public_cross_origin_redirect_without_becoming_credentials()
    {
        var calls = 0;
        var transport = new FakeTransport(_ => calls++ == 0 ? Redirect(302, "http://other.test/") : Ok());
        await using var result = await Send(Client(transport), options: new(new Dictionary<string, string> { ["Accept"] = "application/json" }));
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request => { Assert.False(request.Headers.ContainsKey("Authorization")); Assert.False(request.Headers.ContainsKey("Cookie")); });
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    public async Task Propfind_cannot_be_rewritten_to_get(int status)
    {
        var transport = new FakeTransport(_ => Redirect(status, "/next"));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport), true));
        Assert.Equal("PROPFIND", Assert.Single(transport.Requests).Method);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Propfind_preserves_method_and_depth_on_allowed_redirects(int status)
    {
        var calls = 0;
        var transport = new FakeTransport(_ => calls++ == 0 ? Redirect(status, "/next") : Ok("application/xml"));
        await using var result = await Client(transport).PropFindAsync(new Uri("https://public.test/"), 0, new(), default);
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request => { Assert.Equal("PROPFIND", request.Method); Assert.Equal("0", request.Headers["Depth"]); });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Propfind_depth_is_only_zero_or_one(int depth)
    {
        var transport = new FakeTransport(_ => Ok());
        await Assert.ThrowsAsync<SourceNetworkException>(() => Client(transport).PropFindAsync(new Uri("https://public.test/"), depth, new(), default));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Json_post_preserves_exact_body_content_type_and_credentials_on_safe_redirects(int status)
    {
        var calls = 0;
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8") });
        var transport = new FakeTransport(_ => calls++ == 0 ? Redirect(status, "/next") : Ok());
        var json = Encoding.UTF8.GetBytes("{\"page_size\":100}");
        await using var result = await Post(Client(transport, dns), json, Credentials());
        Assert.Equal(2, dns.Calls);
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal(json, request.Body);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("Bearer hidden-token", request.Headers["Authorization"]);
            Assert.Equal("session=hidden-cookie", request.Headers["Cookie"]);
            Assert.Equal("2022-06-28", request.Headers["Notion-Version"]);
            Assert.False(request.Headers.ContainsKey("Depth"));
        });
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    public async Task Json_post_rejects_method_rewriting_redirects(int status)
    {
        var transport = new FakeTransport(_ => Redirect(status, "/next"));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport), "{}"u8.ToArray()));
        Assert.Equal("POST", Assert.Single(transport.Requests).Method);
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public async Task Json_post_request_body_limit_is_64_KiB_before_DNS_or_transport(int bytes, bool allowed)
    {
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8") });
        var transport = new FakeTransport(_ => Ok());
        var json = Encoding.UTF8.GetBytes("\"" + new string('a', bytes - 2) + "\"");
        if (allowed)
        {
            await using var result = await Post(Client(transport, dns), json);
            Assert.Equal(json, Assert.Single(transport.Requests).Body);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport, dns), json));
            Assert.Equal("Source JSON request exceeds the byte limit.", failure.Message);
            Assert.Equal(0, dns.Calls);
            Assert.Empty(transport.Requests);
        }
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task Json_post_redirect_budget_is_five_hops(int redirects, bool allowed)
    {
        var calls = 0;
        var transport = new FakeTransport(_ => calls++ < redirects ? Redirect(307, "/next") : Ok());
        if (allowed) { await using var result = await Post(Client(transport), "{}"u8.ToArray()); }
        else await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport), "{}"u8.ToArray()));
        Assert.Equal(6, transport.Requests.Count);
        Assert.Equal(6, transport.Disposals);
    }

    [Theory]
    [InlineData("http://public.test/next")]
    [InlineData("https://public.test:444/next")]
    [InlineData("https://other.test/next")]
    public async Task Json_post_credentials_never_reach_an_unsafe_origin(string target)
    {
        var transport = new FakeTransport(_ => Redirect(308, target));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport), "{}"u8.ToArray(), Credentials()));
        Assert.Single(transport.Requests);
        var initial = new FakeTransport(_ => Ok());
        await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(initial), "{}"u8.ToArray(), Credentials(), new Uri("http://public.test/")));
        Assert.Empty(initial.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_post_rejects_mixed_DNS_and_private_redirects(bool redirect)
    {
        var calls = 0;
        var dns = new FakeDns(_ => ++calls == 1 && redirect ? new[] { IPAddress.Parse("8.8.8.8") }
            : new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("3000::1") });
        var transport = new FakeTransport(_ => Redirect(307, "/next"));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport, dns), "{}"u8.ToArray()));
        Assert.Equal(redirect ? 1 : 0, transport.Requests.Count);
    }

    [Fact]
    public async Task Json_post_shares_response_byte_limit_deadline_and_caller_cancellation()
    {
        var transport = new FakeTransport(_ => Body(new NonSeekableStream(new byte[9])));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Post(Client(transport, options: new() { MaxResponseBytes = 8 }), "{}"u8.ToArray()));
        Assert.Equal(1, transport.Disposals);
        var clock = new ManualClock();
        var client = new SafeSourceHttpClient(Policy(), new FakeTransport(_ => Body(new ProbeStream(clock.Fire))),
            Options.Create(new SourceNetworkOptions()), clock);
        Assert.Equal("Source request timed out.", (await Assert.ThrowsAsync<SourceNetworkException>(() => Post(client, "{}"u8.ToArray()))).Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Post(Client(new FakeTransport(_ => Ok())), "{}"u8.ToArray(), ct: cancellation.Token));
    }

    [Fact]
    public async Task Json_post_snapshots_the_body_before_redirects()
    {
        var json = "{}"u8.ToArray();
        var calls = 0;
        var transport = new FakeTransport(_ =>
        {
            if (calls++ > 0) return Ok();
            json[0] = (byte)'[';
            return Redirect(307, "https://other.test/next");
        });
        await using var result = await Post(Client(transport), json);
        Assert.All(transport.Requests, request => Assert.Equal("{}"u8.ToArray(), request.Body));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    [InlineData("application/rss+xml")]
    [InlineData("application/atom+xml")]
    [InlineData("application/xml")]
    public async Task Known_content_types_are_accepted_and_response_disposed(string contentType)
    {
        var transport = new FakeTransport(_ => Ok(contentType));
        await using var result = await Send(Client(transport));
        Assert.Equal(2, result.Length);
        Assert.Equal(contentType, Assert.IsType<SourceContentStream>(result).ContentType);
        Assert.Equal(1, transport.Disposals);
    }

    [Theory]
    [InlineData("application/x-unknown")]
    [InlineData("image/svg+xml")]
    public async Task Unapproved_content_type_is_rejected(string? contentType)
        => await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(new FakeTransport(_ => Ok(contentType)))));

    [Theory]
    [InlineData(null)]
    [InlineData("application/octet-stream")]
    public async Task Missing_or_generic_media_is_bounded_and_preserved_for_adapter_detection(string? contentType)
    {
        await using var result = await Send(Client(new FakeTransport(_ => Ok(contentType))));
        Assert.Equal(contentType, Assert.IsType<SourceContentStream>(result).ContentType);
        Assert.Equal(2, result.Length);
        var transport = new FakeTransport(_ => Ok(contentType));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport, options: new() { MaxResponseBytes = 1 })));
        Assert.Equal(1, transport.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_limit_is_enforced_while_reading_without_content_length(bool propfind)
    {
        var transport = new FakeTransport(_ => Body(new NonSeekableStream(new byte[9])));
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport, options: new() { MaxResponseBytes = 8 }), propfind));
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task Declared_oversize_is_rejected_before_read_and_exact_byte_limit_is_allowed()
    {
        var response = Body(new ProbeStream(() => throw new InvalidOperationException("body must not be read")));
        response.Content.Headers.ContentLength = 9;
        var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(new FakeTransport(_ => response), options: new() { MaxResponseBytes = 8 })));
        Assert.Equal("Source response exceeds the byte limit.", failure.Message);
        await using var result = await Send(Client(new FakeTransport(_ => Ok()), options: new() { MaxResponseBytes = 2 }));
        Assert.Equal(2, result.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_covers_DNS_and_body_reads_and_is_sanitized(bool duringBody)
    {
        var clock = new ManualClock();
        var dns = new FakeDns(_ => { if (!duringBody) clock.Fire(); return new[] { IPAddress.Parse("8.8.8.8") }; });
        var client = new SafeSourceHttpClient(Policy(dns), new FakeTransport(_ => Body(new ProbeStream(clock.Fire))), Options.Create(new SourceNetworkOptions()), clock);
        var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Send(client));
        Assert.Equal("Source request timed out.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new FakeTransport(_ => Ok())).GetAsync(new Uri("https://public.test/"), new(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_cleanup_cannot_outlive_the_total_deadline_even_when_it_blocks_synchronously(bool synchronous)
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new CleanupStream(() =>
        {
            entered.TrySetResult();
            if (synchronous) release.Task.GetAwaiter().GetResult();
            return new ValueTask(release.Task);
        });
        var transport = new FakeTransport(_ => Body(input));
        var client = new SafeSourceHttpClient(Policy(), transport, Options.Create(new SourceNetworkOptions()), clock);
        var operation = Task.Run(() => Send(client));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Fire();
            var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Source request timed out.", failure.Message);
        }
        finally
        {
            release.TrySetResult();
            try { (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_input_cleanup_never_returns_a_buffer(bool callerCancellation)
    {
        var clock = new ManualClock();
        using var caller = new CancellationTokenSource();
        var input = new CleanupStream(() =>
        {
            if (callerCancellation) caller.Cancel();
            else clock.Fire();
            return ValueTask.CompletedTask;
        });
        var transport = new FakeTransport(_ => Body(input));
        var client = new SafeSourceHttpClient(Policy(), transport, Options.Create(new SourceNetworkOptions()), clock);
        var operation = client.GetAsync(new Uri("https://public.test/"), new(), caller.Token);
        if (callerCancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
            Assert.Equal("Source request timed out.", (await Assert.ThrowsAsync<SourceNetworkException>(() => operation)).Message);
    }

    [Fact]
    public async Task Throwing_input_cleanup_is_sanitized_and_still_disposes_the_response_owner()
    {
        var transport = new FakeTransport(_ => Body(new CleanupStream(() => throw new IOException("hidden-token"))));
        var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Send(Client(transport)));
        Assert.Equal("Source network request failed.", failure.Message);
        Assert.DoesNotContain("hidden-token", failure.ToString());
        Assert.Equal(1, transport.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_cannot_mask_deadline_or_caller_cancellation(bool callerCancellation)
    {
        var clock = new ManualClock();
        using var caller = new CancellationTokenSource();
        var transport = new FakeTransport(_ => Body(new CleanupStream(() =>
        {
            if (callerCancellation) caller.Cancel();
            else clock.Fire();
            throw new IOException("hidden-cleanup-failure");
        })));
        var client = new SafeSourceHttpClient(Policy(), transport, Options.Create(new SourceNetworkOptions()), clock);
        var operation = client.GetAsync(new Uri("https://public.test/"), new(), caller.Token);
        if (callerCancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else Assert.Equal("Source request timed out.", (await Assert.ThrowsAsync<SourceNetworkException>(() => operation)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_response_cleanup_is_bounded_for_success_and_redirects(bool redirect)
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(_ => redirect ? Redirect(302, "/next") : Ok(), () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            disposed.TrySetResult();
            throw new IOException("hidden-late-cleanup-error");
        });
        var client = new SafeSourceHttpClient(Policy(), transport, Options.Create(new SourceNetworkOptions()), clock);
        var operation = Task.Run(() => Send(client));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Fire();
            Assert.Equal("Source request timed out.", (await Assert.ThrowsAsync<SourceNetworkException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(5)))).Message);
            Assert.Single(transport.Requests);
        }
        finally
        {
            release.TrySetResult();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Transport_and_status_errors_do_not_disclose_URL_headers_or_body()
    {
        foreach (var transport in new[] { new FakeTransport(_ => throw new HttpRequestException("hidden-token hidden-cookie")), new FakeTransport(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("hidden-token") }) })
        {
            var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => Client(transport).GetAsync(new Uri("https://public.test/?token=hidden-token"), Credentials(), default));
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain("hidden-token", failure.ToString());
            Assert.DoesNotContain("hidden-cookie", failure.ToString());
        }
    }

    [Fact]
    public void Missing_key_or_unsealed_credentials_never_fall_back_to_plaintext()
    {
        using var missing = new SourceSecretProtector(new ConfigurationBuilder().Build());
        var source = new SourceEntity { Config = "{\"authorization\":\"Bearer plaintext\"}" };
        Assert.Throws<SourceNetworkException>(() => SourceRequestOptions.FromSealedConfig(missing, source, "authorization"));
        using var configured = Protector();
        Assert.Throws<SourceNetworkException>(() => SourceRequestOptions.FromSealedConfig(configured, source, "authorization"));
        source.Config = "{}";
        Assert.Throws<SourceNetworkException>(() => SourceRequestOptions.FromSealedConfig(configured, source, "authorization"));
    }

    [Fact]
    public void Discovery_budget_rejects_item_1001_and_defaults_cannot_be_relaxed()
    {
        var options = new SourceNetworkOptions();
        Assert.Empty(options.AllowedCidrs);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(20 * 1024 * 1024, options.MaxResponseBytes);
        Assert.Equal(1000, options.MaxItems);
        var budget = new SourceDiscoveryBudget(Options.Create(options));
        for (var count = 0; count < 1000; count++) budget.CountItem();
        Assert.Throws<SourceNetworkException>(budget.CountItem);
        Assert.Throws<SourceNetworkException>(() => Policy(options: new() { MaxItems = 1001 }));
        Assert.Throws<SourceNetworkException>(() => Policy(options: new() { MaxResponseBytes = 20 * 1024 * 1024 + 1 }));
        Assert.Throws<SourceNetworkException>(() => Policy(options: new() { RequestTimeout = TimeSpan.FromSeconds(31) }));
    }

    [Fact]
    public async Task Real_transport_uses_pinned_IP_original_host_and_no_automatic_redirects()
    {
        var sockets = new FakeSocketConnector();
        var dns = new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8") });
        var client = new SafeSourceHttpClient(Policy(dns), new SourceHttpTransport(sockets), Options.Create(new SourceNetworkOptions()), TimeProvider.System);
        var failure = await Assert.ThrowsAsync<SourceNetworkException>(() => client.GetAsync(new Uri("http://public.test/"), new(), default));
        Assert.Equal("Source target is not allowed.", failure.Message);
        Assert.Equal("8.8.8.8", Assert.Single(sockets.Targets).Address.ToString());
        Assert.Equal(80, sockets.Targets[0].Port);
        Assert.Contains("Host: public.test", Encoding.ASCII.GetString(sockets.Stream.Writes.ToArray()));
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public void Handler_keeps_TLS_validation_and_disables_proxy_cookies_and_redirects()
    {
        using var handler = new SourceHttpTransport(new FakeSocketConnector()).CreateHandler(new Uri("https://public.test/"), new[] { IPAddress.Parse("8.8.8.8") });
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task HTTPS_transport_sends_original_host_in_TLS_SNI_not_pinned_IP()
    {
        var sockets = new FakeSocketConnector();
        var client = new SafeSourceHttpClient(Policy(), new SourceHttpTransport(sockets),
            Options.Create(new SourceNetworkOptions()), TimeProvider.System);
        await Assert.ThrowsAsync<SourceNetworkException>(() => Send(client));
        Assert.Equal("8.8.8.8", Assert.Single(sockets.Targets).Address.ToString());
        Assert.Equal(443, sockets.Targets[0].Port);
        Assert.Contains("public.test", Encoding.ASCII.GetString(sockets.Stream.Writes.ToArray()));
    }

    [Theory]
    [InlineData("public.test", true)]
    [InlineData("wrong.test", false)]
    public async Task Pinned_TLS_validates_the_original_host_certificate_name(string certificateHost, bool allowed)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Source test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={certificateHost}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(certificateHost);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var publicLeaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), new byte[] { 1 });
        using var ephemeralLeaf = publicLeaf.CopyWithPrivateKey(leafKey);
        using var leaf = X509CertificateLoader.LoadPkcs12(ephemeralLeaf.Export(X509ContentType.Pkcs12), null);
        await using var sockets = new TlsSocketConnector(leaf);
        using var handler = new SourceHttpTransport(sockets).CreateHandler(new Uri("https://public.test/"), new[] { IPAddress.Parse("8.8.8.8") });
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(root);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new HttpClient(handler);
        if (allowed)
        {
            HttpResponseMessage received;
            try { received = await client.GetAsync("https://public.test/").WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (HttpRequestException exception) { throw new InvalidOperationException("In-memory TLS handshake failed.", sockets.ServerError ?? exception); }
            using var response = received;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{}", await response.Content.ReadAsStringAsync());
            Assert.Contains("Host: public.test", sockets.RequestHeaders);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://public.test/").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsType<AuthenticationException>(failure.InnerException);
            Assert.Empty(sockets.RequestHeaders);
        }
        Assert.Equal("public.test", sockets.ServerName);
        Assert.Equal("8.8.8.8", Assert.Single(sockets.Targets).Address.ToString());
        Assert.Equal(443, sockets.Targets[0].Port);
    }

    [Fact]
    public void DI_uses_managed_network_configuration_and_exposes_safe_client()
    {
        using var app = new AuthTestWebApplicationFactory();
        Assert.IsType<SafeSourceHttpClient>(app.Services.GetRequiredService<ISafeSourceHttpClient>());
        Assert.IsType<SourceDnsResolver>(app.Services.GetRequiredService<ISourceDnsResolver>());
        Assert.IsType<SourceHttpTransport>(app.Services.GetRequiredService<ISourceHttpTransport>());
        Assert.Empty(app.Services.GetRequiredService<IOptions<SourceNetworkOptions>>().Value.AllowedCidrs);
        Assert.NotSame(app.Services.GetRequiredService<SourceDiscoveryBudget>(), app.Services.GetRequiredService<SourceDiscoveryBudget>());
    }

    [Fact]
    public void Sealed_credentials_are_bound_to_source_identity_and_tampering_fails()
    {
        using var protector = Protector();
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = Guid.NewGuid() };
        source.Config = JsonSerializer.Serialize(new { authorization = protector.Seal("Bearer hidden-token",
            $"{source.KnowledgeSystemId:D}:{source.Id:D}:authorization") });
        source.Id = Guid.NewGuid();
        var failure = Assert.Throws<SourceNetworkException>(() => SourceRequestOptions.FromSealedConfig(protector, source, "authorization"));
        Assert.DoesNotContain("hidden-token", failure.ToString());
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void Source_service_only_decrypts_registered_secret_fields()
    {
        using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SourceService>();
        Assert.Throws<SourceNetworkException>(() => service.CreateRequestOptions(
            new SourceEntity { Kind = SourceKind.Folder }, authorizationField: "unregistered"));
        Assert.Throws<SourceNetworkException>(() => service.CreateRequestOptions(
            new SourceEntity { Kind = SourceKind.AzureBlob }));
        Assert.NotNull(service.CreateRequestOptions(new SourceEntity { Kind = SourceKind.Folder }));
    }

    [Fact]
    public void Adapter_contract_exposes_no_raw_HttpClient()
    {
        Assert.Equal(new[] { "GetAsync", "PostJsonAsync", "PropFindAsync" }, typeof(ISafeSourceHttpClient).GetMethods().Select(method => method.Name).Order().ToArray());
        Assert.All(typeof(ISafeSourceHttpClient).GetMethods(), method => Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(HttpClient)));
    }

    [Theory]
    [InlineData("azure_blob")]
    [InlineData("memory")]
    [InlineData("upload")]
    public void Reserved_kinds_cannot_be_registered(string kind)
    {
        var registry = new SourceAdapterRegistry(new[] { new SourceKindDescriptor("folder", false, Array.Empty<SourceConfigField>()), new SourceKindDescriptor(kind, true, Array.Empty<SourceConfigField>()) });
        Assert.Equal("folder", Assert.Single(registry.CreatableKinds).Kind);
        Assert.False(registry.TryGet(kind, out _));
    }

    [Theory]
    [InlineData("azure_blob")]
    [InlineData("memory")]
    [InlineData("upload")]
    public async Task Reserved_existing_sources_are_not_scheduled_or_dispatched(string kind)
    {
        using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        using var db = app.CreateDbContext();
        var knowledgeSystem = new KnowledgeSystemEntity { Name = $"network-{Guid.NewGuid():N}" };
        db.KnowledgeSystems.Add(knowledgeSystem);
        var source = new SourceEntity { KnowledgeSystemId = knowledgeSystem.Id, Kind = kind, Name = "Reserved", SyncIntervalMinutes = 1, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        Assert.Equal(0, await app.Services.GetRequiredService<SourceSyncScheduler>().ScheduleDueSourcesAsync(default));
        db.SourceSyncJobs.Add(new SourceSyncJobEntity { SourceId = source.Id, Status = "queued" });
        await db.SaveChangesAsync();
        Assert.False(await app.Services.GetRequiredService<SourceSyncWorker>().RunNextAsync(default));
        Assert.False(await db.SourceSyncRuns.AnyAsync(run => run.SourceId == source.Id));
    }

    private static Task<Stream> Send(SafeSourceHttpClient client, bool propfind = false, SourceRequestOptions? options = null)
        => propfind ? client.PropFindAsync(new Uri("https://public.test/"), 1, options ?? new(), default) : client.GetAsync(new Uri("https://public.test/"), options ?? new(), default);
    private static Task<Stream> Post(ISafeSourceHttpClient client, ReadOnlyMemory<byte> json, SourceRequestOptions? options = null,
        Uri? uri = null, CancellationToken ct = default)
    {
        var method = typeof(ISafeSourceHttpClient).GetMethod("PostJsonAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<Stream>), method.ReturnType);
        Assert.Equal(new[] { typeof(Uri), typeof(ReadOnlyMemory<byte>), typeof(SourceRequestOptions), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        return client.PostJsonAsync(uri ?? new Uri("https://public.test/"), json, options ?? new(), ct);
    }
    private static SourceNetworkPolicy Policy(FakeDns? dns = null, SourceNetworkOptions? options = null) => new(dns ?? new FakeDns(_ => new[] { IPAddress.Parse("8.8.8.8") }), Options.Create(options ?? new()));
    private static SafeSourceHttpClient Client(FakeTransport transport, FakeDns? dns = null, SourceNetworkOptions? options = null) => new(Policy(dns, options), transport, Options.Create(options ?? new()), TimeProvider.System);
    private static SourceSecretProtector Protector() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());
    private static SourceRequestOptions Credentials()
    {
        using var protector = Protector();
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = Guid.NewGuid() };
        source.Config = JsonSerializer.Serialize(new Dictionary<string, string> { ["authorization"] = protector.Seal("Bearer hidden-token", $"{source.KnowledgeSystemId:D}:{source.Id:D}:authorization"), ["cookie"] = protector.Seal("session=hidden-cookie", $"{source.KnowledgeSystemId:D}:{source.Id:D}:cookie") });
        return SourceRequestOptions.FromSealedConfig(protector, source, "authorization", "cookie", new Dictionary<string, string> { ["Accept"] = "application/json", ["If-None-Match"] = "\"version\"", ["Notion-Version"] = "2022-06-28" });
    }
    private static HttpResponseMessage Ok(string? contentType = "application/json")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
        if (contentType is not null) response.Content.Headers.ContentType = new(contentType);
        return response;
    }
    private static HttpResponseMessage Body(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
    private static HttpResponseMessage Redirect(int status, string target)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }
    private sealed class FakeDns(Func<string, IPAddress[]> resolve) : ISourceDnsResolver
    {
        public int Calls { get; private set; }
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(resolve(host)); }
    }
    private sealed record SentRequest(string Method, Dictionary<string, string> Headers, byte[] Body, string? ContentType);
    private sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> send, Action? cleanup = null) : ISourceHttpTransport
    {
        public List<SentRequest> Requests { get; } = new();
        public List<IReadOnlyList<IPAddress>> Addresses { get; } = new();
        public int Disposals { get; private set; }
        public async Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method.Method, request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct), request.Content?.Headers.ContentType?.ToString()));
            Addresses.Add(addresses);
            return new SourceHttpResponse(send(request), new DisposeAction(() => { Disposals++; cleanup?.Invoke(); }));
        }
    }
    private sealed class DisposeAction(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { Assert.Equal(TimeSpan.FromSeconds(30), dueTime); _callback = callback; _state = state; return new ManualTimer(); }
        public void Fire() => _callback!(_state);
        private sealed class ManualTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class ProbeStream(Action beforeRead) : MemoryStream(new byte[1])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { beforeRead(); ct.ThrowIfCancellationRequested(); return base.ReadAsync(buffer, ct); }
    }
    private sealed class CleanupStream(Func<ValueTask> cleanup) : MemoryStream(new byte[1])
    {
        public override ValueTask DisposeAsync() => cleanup();
    }
    private sealed class FakeSocketConnector : ISourceSocketConnector
    {
        public List<IPEndPoint> Targets { get; } = new();
        public DuplexStream Stream { get; } = new();
        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct) { Targets.Add(new(address, port)); return ValueTask.FromResult<Stream>(Stream); }
    }
    private sealed class TlsSocketConnector(X509Certificate2 certificate) : ISourceSocketConnector, IAsyncDisposable
    {
        private Stream? _client;
        private Stream? _server;
        private Task? _serve;
        public List<IPEndPoint> Targets { get; } = new();
        public string? ServerName { get; private set; }
        public string RequestHeaders { get; private set; } = "";
        public Exception? ServerError { get; private set; }

        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Targets.Add(new(address, port));
            var requests = new Pipe();
            var responses = new Pipe();
            _client = new PairedStream(responses.Reader.AsStream(), requests.Writer.AsStream());
            _server = new PairedStream(requests.Reader.AsStream(), responses.Writer.AsStream());
            _serve = ServeAsync(_server);
            return ValueTask.FromResult(_client);
        }

        private async Task ServeAsync(Stream connection)
        {
            using var tls = new SslStream(connection);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { ServerName = name; return certificate; },
                    EnabledSslProtocols = SslProtocols.Tls12,
                });
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync() is { Length: > 0 } line) RequestHeaders += line + "\r\n";
                await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray());
                await tls.FlushAsync();
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException or ObjectDisposedException) { ServerError = exception; }
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            _server?.Dispose();
            if (_serve is not null) await _serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private sealed class PairedStream(Stream reader, Stream writer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => reader.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => writer.WriteAsync(buffer, ct);
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken ct) => writer.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { reader.Dispose(); writer.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _reads = new(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        public MemoryStream Writes { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _reads.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _reads.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => Writes.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => Writes.WriteAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
