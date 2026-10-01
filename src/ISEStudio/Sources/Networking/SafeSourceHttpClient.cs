using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Networking;

public interface ISafeSourceHttpClient
{
    Task<Stream> GetAsync(Uri uri, SourceRequestOptions options, CancellationToken ct);
    Task<Stream> PropFindAsync(Uri uri, int depth, SourceRequestOptions options, CancellationToken ct);
    Task<Stream> PostJsonAsync(Uri uri, ReadOnlyMemory<byte> json, SourceRequestOptions options, CancellationToken ct)
        => Task.FromException<Stream>(new SourceNetworkException("Source JSON POST is not supported."));
}

public sealed class SourceContentStream(string? contentType, Uri? finalUri = null, string? linkHeader = null) : MemoryStream
{
    public string? ContentType { get; } = contentType;
    public Uri? FinalUri { get; } = finalUri;
    public string? LinkHeader { get; } = linkHeader;
}

public sealed class SourceRequestOptions
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "If-None-Match", "Notion-Version" };
    private readonly IReadOnlyDictionary<string, string> _headers;
    private string? _authorization;
    private string? _cookie;
    private Uri? _webDavRoot;

    public static SourceRequestOptions Empty { get; } = new();
    internal bool HasCredentials => _authorization is not null || _cookie is not null;

    public SourceRequestOptions(IReadOnlyDictionary<string, string>? headers = null)
    {
        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                if (!AllowedHeaders.Contains(header.Key))
                    throw new SourceNetworkException("Source request header is not allowed.");
                ValidateValue(header.Value);
                if (!validated.TryAdd(header.Key, header.Value))
                    throw new SourceNetworkException("Source request header is duplicated.");
            }
        }
        _headers = validated;
    }

    public static SourceRequestOptions FromSealedConfig(ISourceSecretProtector protector, SourceEntity source,
        string? authorizationField = null, string? cookieField = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var result = new SourceRequestOptions(headers);
        if (authorizationField is null && cookieField is null) return result;
        if (!protector.IsConfigured)
            throw new SourceNetworkException("Source credentials are unavailable.");
        try
        {
            using var config = JsonDocument.Parse(source.Config);
            result._authorization = Open(authorizationField);
            result._cookie = Open(cookieField);
            if (result._authorization is not null && !AuthenticationHeaderValue.TryParse(result._authorization, out _))
                throw new SourceNetworkException("Source credentials are invalid.");
            return result;

            string? Open(string? field)
            {
                if (field is null) return null;
                if (!config.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } ciphertext || !ciphertext.StartsWith("v1:", StringComparison.Ordinal))
                    throw new SourceNetworkException("Source credentials are unavailable.");
                var plaintext = protector.Open(ciphertext, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field}");
                ValidateValue(plaintext);
                if (string.IsNullOrWhiteSpace(plaintext)) throw new SourceNetworkException("Source credentials are invalid.");
                return plaintext;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or InvalidOperationException)
        {
            throw new SourceNetworkException("Source credentials are unavailable.");
        }
    }

    internal SourceRequestOptions WithinWebDavRoot(Uri root)
    {
        var validated = Adapters.ContentSourceConfig.WebDavHref(root, root, root.OriginalString);
        return new SourceRequestOptions(_headers)
        {
            _authorization = _authorization,
            _cookie = _cookie,
            _webDavRoot = validated,
        };
    }

    internal void ValidateTarget(Uri uri, string? redirect = null)
    {
        if (_webDavRoot is not null)
            _ = Adapters.ContentSourceConfig.WebDavHref(_webDavRoot, uri, redirect ?? uri.OriginalString);
    }

    internal void Apply(HttpRequestMessage request)
    {
        try
        {
            foreach (var header in _headers) request.Headers.Add(header.Key, header.Value);
            if (_authorization is not null) request.Headers.Add("Authorization", _authorization);
            if (_cookie is not null) request.Headers.Add("Cookie", _cookie);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new SourceNetworkException("Source request headers are invalid.");
        }
    }

    private static void ValidateValue(string value)
    {
        if (value is null || value.Length > 8192 || value.Any(character => character < 32 || character == 127))
            throw new SourceNetworkException("Source request header value is invalid.");
    }

    internal static SourceRequestOptions FromSealedContentConfig(ISourceSecretProtector protector, SourceEntity source)
    {
        using var config = JsonDocument.Parse(source.Config);
        if (source.Kind == SourceKind.WebDav && !config.RootElement.TryGetProperty("username", out _)) return Empty;
        if (!protector.IsConfigured) throw new SourceNetworkException("Source credentials are unavailable.");
        try
        {
            var result = new SourceRequestOptions(source.Kind == SourceKind.Notion
                ? new Dictionary<string, string> { ["Notion-Version"] = Adapters.ContentSourceConfig.NotionVersion, ["Accept"] = "application/json" }
                : new Dictionary<string, string> { ["Accept"] = "application/xml, text/plain, application/octet-stream" });
            if (source.Kind == SourceKind.Notion)
            {
                var token = Open("token");
                if (token.Any(char.IsWhiteSpace)) throw new FormatException();
                result._authorization = "Bearer " + token;
            }
            else if (source.Kind == SourceKind.WebDav)
            {
                var username = Open("username");
                if (username.Contains(':')) throw new FormatException();
                result._authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + Open("password")));
            }
            else throw new FormatException();
            ValidateValue(result._authorization!);
            return result;

            string Open(string field)
            {
                var ciphertext = config.RootElement.GetProperty(field).GetString()!;
                if (!ciphertext.StartsWith("v1:", StringComparison.Ordinal)) throw new FormatException();
                var plaintext = protector.Open(ciphertext, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field}");
                ValidateValue(plaintext);
                if (string.IsNullOrWhiteSpace(plaintext)) throw new FormatException();
                return plaintext;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SourceNetworkException("Source credentials are unavailable.");
        }
    }
}

public interface ISourceHttpTransport
{
    Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct);
}

public sealed class SourceHttpResponse(HttpResponseMessage message, IDisposable? owner = null) : IDisposable
{
    public HttpResponseMessage Message { get; } = message;
    public void Dispose()
    {
        try { Message.Dispose(); }
        finally { owner?.Dispose(); }
    }
}

public interface ISourceSocketConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct);
}

public sealed class SourceSocketConnector : ISourceSocketConnector
{
    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class SourceHttpTransport(ISourceSocketConnector sockets) : ISourceHttpTransport
{
    public SocketsHttpHandler CreateHandler(Uri uri, IReadOnlyList<IPAddress> addresses)
    {
        var pinned = addresses.ToArray();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32,
            ConnectCallback = async (context, ct) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, uri.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                    || context.DnsEndPoint.Port != uri.Port)
                    throw new SourceNetworkException("Source connection target is invalid.");
                foreach (var address in pinned)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        return await sockets.ConnectAsync(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address,
                            uri.Port, ct).ConfigureAwait(false);
                    }
                    catch (SocketException) { }
                }
                throw new SourceNetworkException("Source connection failed.");
            },
        };
    }

    public async Task<SourceHttpResponse> SendAsync(HttpRequestMessage request, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        var client = new HttpClient(CreateHandler(request.RequestUri!, addresses)) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return new SourceHttpResponse(response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed class SafeSourceHttpClient : ISafeSourceHttpClient
{
    private static readonly HashSet<string> AcceptedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain", "text/html", "text/markdown", "text/csv", "text/xml",
        "application/json", "application/xml", "application/xhtml+xml", "application/rss+xml", "application/atom+xml",
        "application/pdf", "application/msword", "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };
    private readonly SourceNetworkPolicy _policy;
    private readonly ISourceHttpTransport _transport;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly int _maxBytes;

    public SafeSourceHttpClient(SourceNetworkPolicy policy, ISourceHttpTransport transport,
        IOptions<SourceNetworkOptions> options, TimeProvider clock)
    {
        options.Value.Validate();
        _policy = policy;
        _transport = transport;
        _clock = clock;
        _timeout = options.Value.RequestTimeout;
        _maxBytes = options.Value.MaxResponseBytes;
    }

    public Task<Stream> GetAsync(Uri uri, SourceRequestOptions options, CancellationToken ct)
        => SendAsync(uri, HttpMethod.Get, null, options, ct);

    public Task<Stream> PropFindAsync(Uri uri, int depth, SourceRequestOptions options, CancellationToken ct)
        => depth is 0 or 1 ? SendAsync(uri, new HttpMethod("PROPFIND"), depth, options, ct)
            : Task.FromException<Stream>(new SourceNetworkException("Source PROPFIND depth must be zero or one."));

    public Task<Stream> PostJsonAsync(Uri uri, ReadOnlyMemory<byte> json, SourceRequestOptions options, CancellationToken ct)
        => json.Length > 64 * 1024
            ? Task.FromException<Stream>(new SourceNetworkException("Source JSON request exceeds the byte limit."))
            : SendAsync(uri, HttpMethod.Post, null, options, ct, json.ToArray());

    private async Task<Stream> SendAsync(Uri uri, HttpMethod method, int? depth, SourceRequestOptions options,
        CancellationToken ct, byte[]? json = null)
    {
        using var deadline = new CancellationTokenSource(_timeout, _clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = cancellation.Token;
        try
        {
            for (var hops = 0; ; hops++)
            {
                token.ThrowIfCancellationRequested();
                options.ValidateTarget(uri);
                if (options.HasCredentials && uri.Scheme != Uri.UriSchemeHttps)
                    throw new SourceNetworkException("Source credentials require same-origin HTTPS.");
                var addresses = await _policy.ValidateAsync(uri, token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.UserAgent.ParseAdd("ISEStudio-source-connectors/1.0");
                options.Apply(request);
                if (depth.HasValue) request.Headers.Add("Depth", depth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (json is not null)
                {
                    request.Content = new ByteArrayContent(json);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                var response = await _transport.SendAsync(request, addresses, token).ConfigureAwait(false);
                Stream? input = null;
                MemoryStream? output = null;
                try
                {
                    try
                    {
                        var message = response.Message;
                        var status = (int)message.StatusCode;
                        if (status is >= 300 and < 400)
                        {
                            if (hops >= 5 || message.Headers.Location is null
                                || (method != HttpMethod.Get ? status is not (307 or 308) : status is not (301 or 302 or 303 or 307 or 308)))
                                throw new SourceNetworkException("Source redirect is not allowed.");
                            options.ValidateTarget(uri, message.Headers.Location.OriginalString);
                            var next = new Uri(uri, message.Headers.Location);
                            if (options.HasCredentials && !SameOrigin(uri, next))
                                throw new SourceNetworkException("Source credentials require same-origin HTTPS.");
                            uri = next;
                            continue;
                        }
                        if (!message.IsSuccessStatusCode)
                            throw new SourceNetworkException("Source upstream request failed.");
                        if (message.StatusCode == HttpStatusCode.PartialContent || message.Content.Headers.Contains("Content-Range"))
                            throw new SourceNetworkException("Source response is not a complete representation.");
                        var mediaType = message.Content.Headers.ContentType?.MediaType;
                        if (mediaType is not null && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) && !AcceptedContentTypes.Contains(mediaType)
                            || message.Content.Headers.ContentEncoding.Count != 0)
                            throw new SourceNetworkException("Source response content type or encoding is not supported.");
                        if (message.Content.Headers.ContentLength > _maxBytes)
                            throw new SourceNetworkException("Source response exceeds the byte limit.");

                        input = await message.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        var link = message.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null;
                        if (link is not null && (link.Length > 8192 || link.Any(char.IsControl)))
                            throw new SourceNetworkException("Source response pagination metadata is invalid.");
                        output = new SourceContentStream(mediaType, uri, link);
                        var buffer = new byte[81920];
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                                _maxBytes - checked((int)output.Length) + 1)), token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (count == 0) break;
                            if (output.Length + count > _maxBytes)
                                throw new SourceNetworkException("Source response exceeds the byte limit.");
                            output.Write(buffer, 0, count);
                        }
                    }
                    finally
                    {
                        await CleanupResponseAsync(response, input, token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    output.Position = 0;
                    return output;
                }
                catch
                {
                    output?.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new SourceNetworkException("Source request timed out.");
        }
        catch (SourceNetworkException) { throw; }
        catch (Exception)
        {
            throw new SourceNetworkException("Source network request failed.");
        }
    }

    private static async Task CleanupResponseAsync(SourceHttpResponse response, Stream? input, CancellationToken token)
    {
        var cleanup = Task.Run(async () =>
        {
            try
            {
                if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }, CancellationToken.None);
        _ = cleanup.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await cleanup.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static bool SameOrigin(Uri first, Uri second)
        => second.IsAbsoluteUri && second.Scheme == Uri.UriSchemeHttps && first.Scheme == second.Scheme
            && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;
}