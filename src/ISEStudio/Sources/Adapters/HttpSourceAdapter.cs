using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public abstract class HttpSourceAdapter : ISourceAdapter
{
    private readonly SourceService _sources;
    private readonly SourceNetworkOptions _limits;
    protected ISafeSourceHttpClient Http { get; }
    public abstract string Kind { get; }

    protected HttpSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    {
        Http = http;
        _sources = sources;
        _limits = options.Value;
        _limits.Validate();
    }

    public async Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_limits.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ScanBudget? budget = null;
        try
        {
            using var document = JsonDocument.Parse(source.Config);
            var config = document.RootElement;
            if (!string.Equals(source.Kind, Kind, StringComparison.OrdinalIgnoreCase)
                || (Kind is SourceKind.WebDav or SourceKind.Notion
                    ? ContentSourceConfig.Validate(Kind, config)
                    : HttpSourceConfig.Validate(Kind, config, allowFragment: true)) is not null)
                throw new SourceNetworkException("Source connector config is invalid.");
            var authenticated = Kind == SourceKind.Notion || config.TryGetProperty(Kind == SourceKind.WebDav ? "username" : "auth_header", out _);
            var options = Kind is SourceKind.WebDav or SourceKind.Notion
                ? _sources.CreateContentRequestOptions(source)
                : _sources.CreateRequestOptions(source, authenticated ? "auth_header" : null);
            budget = new ScanBudget(_limits, config.TryGetProperty("max_pages", out var maximum) ? maximum.GetInt32() : 100);
            var result = await DiscoverCoreAsync(config, options, authenticated, budget, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new SourceScan(Enumerate(result.Items), result.IsComplete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failure = new SourceNetworkException(exception is OperationCanceledException
                ? "Source discovery timed out." : "Source discovery failed.");
            if (budget is null || budget.Items.Count == 0) throw failure;
            return new SourceScan(Enumerate(budget.Items, failure), IsComplete: false);
        }
    }

    protected abstract Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct);

    protected async Task<byte[]> FetchAsync(Uri uri, SourceRequestOptions options, ScanBudget budget, CancellationToken ct)
        => (await FetchContentAsync(uri, options, budget, ct).ConfigureAwait(false)).Bytes;

    protected async Task<FetchedResponse> FetchResponseAsync(Uri uri, SourceRequestOptions options, ScanBudget budget, CancellationToken ct)
    {
        await using var response = await Http.GetAsync(uri, options, ct).ConfigureAwait(false);
        var content = await ReadContentAsync(response, budget, ct).ConfigureAwait(false);
        var metadata = response as SourceContentStream;
        return new(content.Bytes, content.ContentType, metadata?.FinalUri ?? uri, metadata?.LinkHeader, metadata?.FinalUri is not null);
    }

    protected sealed record FetchedResponse(byte[] Bytes, string? ContentType, Uri FinalUri, string? LinkHeader, bool HasMetadata);

    protected async Task<(byte[] Bytes, string? ContentType)> FetchContentAsync(Uri uri, SourceRequestOptions options, ScanBudget budget, CancellationToken ct)
    {
        await using var response = await Http.GetAsync(uri, options, ct).ConfigureAwait(false);
        return await ReadContentAsync(response, budget, ct).ConfigureAwait(false);
    }

    protected static async Task<(byte[] Bytes, string? ContentType)> ReadContentAsync(Stream response, ScanBudget budget, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await response.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, budget.RemainingBytes + 1)), ct).ConfigureAwait(false);
            if (count == 0) break;
            budget.CountBytes(count);
            await body.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (body.Length == 0) throw new SourceNetworkException("Source response body is empty.");
        return (body.ToArray(), (response as SourceContentStream)?.ContentType);
    }

    protected static DateTimeOffset? Timestamp(string? raw)
    {
        if (raw is null) return null;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            throw new FormatException();
        return value;
    }

    protected static string PageMime(Uri uri, byte[] body, string? contentType = null)
    {
        if (contentType is not null && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var accepted = contentType.ToLowerInvariant();
            _ = Filename("document", accepted);
            return accepted;
        }
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (body.AsSpan().StartsWith("%PDF-"u8)) return "application/pdf";
        if (body.AsSpan().StartsWith("PK\x03\x04"u8))
        {
            using var input = new MemoryStream(body, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count > 256 || archive.GetEntry("[Content_Types].xml") is null) throw new FormatException();
            var word = archive.GetEntry("word/document.xml") is not null;
            var excel = archive.GetEntry("xl/workbook.xml") is not null;
            if (word == excel) throw new FormatException();
            return word ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        }
        return extension switch
        {
            ".md" => "text/markdown", ".txt" => "text/plain", ".csv" => "text/csv",
            ".json" => "application/json", ".xml" => "application/xml",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 1024))).TrimStart().StartsWith('<') ? "text/html" : "text/plain",
        };
    }

    protected static string Filename(string title, string mime)
    {
        var extension = mime switch
        {
            "text/html" or "application/xhtml+xml" => ".html", "text/markdown" => ".md",
            "text/plain" => ".txt", "text/csv" => ".csv", "application/json" => ".txt",
            "application/xml" or "text/xml" => ".txt", "application/pdf" => ".pdf",
            "application/msword" => ".doc", "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            _ => throw new FormatException(),
        };
        var safe = new string(title.Take(120).Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray()).Trim('.', '-');
        if (safe.Length == 0) safe = "document";
        return safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? safe : safe + extension;
    }

    protected sealed record DiscoveredItem(string Key, string Filename, string Mime, byte[] Bytes, DateTimeOffset? Time);
    protected sealed record DiscoveryResult(IReadOnlyList<DiscoveredItem> Items, bool IsComplete);

    protected sealed class ScanBudget(SourceNetworkOptions limits, int maxPages)
    {
        public List<DiscoveredItem> Items { get; } = [];
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pages = new(StringComparer.Ordinal);
        private int _requests;
        private int _nodes;
        public int RemainingBytes { get; private set; } = limits.MaxResponseBytes;

        public void Node()
        {
            if (++_nodes > limits.MaxItems) throw new SourceNetworkException("Source discovery exceeds the item limit.");
        }

        public void Page(string identity)
        {
            if (!_pages.Add(identity))
                throw new SourceNetworkException("Source discovery exceeds the page limit or contains a cycle.");
            RequestPage();
        }

        public void RequestPage()
        {
            if (++_requests > maxPages)
                throw new SourceNetworkException("Source discovery exceeds the page limit.");
        }

        public void CountBytes(int count)
        {
            if (count > RemainingBytes) throw new SourceNetworkException("Source discovery exceeds the byte limit.");
            RemainingBytes -= count;
        }

        public void Page(Uri uri)
            => Page(uri.AbsoluteUri);

        public string Item(string raw)
        {
            var key = HttpSourceConfig.Key(raw);
            if (!_keys.Add(key)) throw new SourceNetworkException("Source discovery contains duplicate identities.");
            if (_keys.Count > limits.MaxItems) throw new SourceNetworkException("Source discovery exceeds the item limit.");
            return key;
        }
    }

    private static async IAsyncEnumerable<SourceItem> Enumerate(IReadOnlyList<DiscoveredItem> items,
        SourceNetworkException? failure = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new SourceItem(item.Key, item.Filename, item.Mime, new MemoryStream(item.Bytes, writable: false), item.Time);
        }
        ct.ThrowIfCancellationRequested();
        if (failure is not null) throw failure;
        await Task.CompletedTask;
    }
}