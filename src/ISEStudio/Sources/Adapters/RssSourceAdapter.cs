using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class RssSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    public override string Kind => SourceKind.Rss;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var origin = HttpSourceConfig.Normalize(config.GetProperty("feed_url").GetString()!, allowQuery: false);
        var current = origin;
        var items = budget.Items;
        while (true)
        {
            budget.Page(current);
            var response = await FetchResponseAsync(current, options, budget, ct).ConfigureAwait(false);
            var body = response.Bytes;
            current = response.FinalUri;
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true, MaxCharactersInDocument = body.Length });
            var feed = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
            var root = feed.Root ?? throw new FormatException();
            var atom = root.Name == Atom + "feed";
            if (!atom && (root.Name != "rss" || root.Element("channel") is null)) throw new FormatException();
            var container = atom ? root : root.Element("channel")!;
            var entries = container.Elements(atom ? Atom + "entry" : "item");
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var linkElement = atom ? entry.Elements(Atom + "link").FirstOrDefault(IsArticleLink) : entry.Element("link");
                var link = atom ? linkElement?.Attribute("href")?.Value : linkElement?.Value;
                if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(ElementBase(linkElement!, current), link.Trim(), out var parsed)) throw new FormatException();
                var uri = HttpSourceConfig.Normalize(parsed.AbsoluteUri, allowQuery: true);
                var identity = entry.Element(atom ? Atom + "id" : "guid")?.Value;
                var key = budget.Item(string.IsNullOrWhiteSpace(identity) ? uri.AbsoluteUri : identity);
                var title = entry.Element(atom ? Atom + "title" : "title")?.Value ?? "article";
                var time = Timestamp(atom ? entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value
                    : entry.Element("pubDate")?.Value ?? entry.Element(XName.Get("date", "http://purl.org/dc/elements/1.1/"))?.Value);
                var sameOrigin = uri.Scheme == origin.Scheme && uri.IdnHost == origin.IdnHost && uri.Port == origin.Port;
                var (article, contentType) = await FetchContentAsync(uri, authenticated && !sameOrigin ? SourceRequestOptions.Empty : options, budget, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(article))) throw new FormatException();
                var mime = PageMime(uri, article, contentType);
                items.Add(new(key, Filename(title, mime), mime, article, time));
            }
            var nextLinks = container.Elements(Atom + "link").Where(link => (string?)link.Attribute("rel") == "next").ToArray();
            if (nextLinks.Length == 0) return new(items, true);
            if (nextLinks.Length != 1 || string.IsNullOrWhiteSpace((string?)nextLinks[0].Attribute("href"))) throw new FormatException();
            current = HttpSourceConfig.Continue(origin, ElementBase(nextLinks[0], current), nextLinks[0].Attribute("href")!.Value);
        }
    }

    private static bool IsArticleLink(XElement link)
        => ((string?)link.Attribute("rel") is null or "alternate")
            && ((string?)link.Attribute("type") is null or "text/html" or "application/xhtml+xml");

    private static Uri ElementBase(XElement element, Uri fallback)
    {
        var current = fallback;
        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
        {
            if (ancestor.Attribute(XNamespace.Xml + "base") is { } xmlBase)
            {
                if (!Uri.TryCreate(current, xmlBase.Value, out var resolved)) throw new FormatException();
                current = HttpSourceConfig.Normalize(resolved.AbsoluteUri, allowQuery: true);
            }
        }
        return current;
    }
}