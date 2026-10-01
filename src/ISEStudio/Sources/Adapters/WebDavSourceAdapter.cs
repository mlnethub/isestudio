using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class WebDavSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    private static readonly XNamespace Dav = "DAV:";
    public override string Kind => SourceKind.WebDav;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var root = ContentSourceConfig.WebDavRoot(config);
        options = options.WithinWebDavRoot(root);
        var maximumDepth = ContentSourceConfig.Depth(config);
        var queue = new Queue<(Uri Directory, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.AbsoluteUri };
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var current))
        {
            budget.Page(current.Directory);
            await using var response = await Http.PropFindAsync(current.Directory, 1, options, ct).ConfigureAwait(false);
            var body = await ReadContentAsync(response, budget, ct).ConfigureAwait(false);
            using var reader = XmlReader.Create(new MemoryStream(body.Bytes, writable: false), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = body.Bytes.Length, MaxCharactersFromEntities = 0 });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != Dav + "multistatus") throw new FormatException();
            var selfSeen = false;
            foreach (var entry in document.Root.Elements())
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name != Dav + "response") throw new FormatException();
                var href = ContentSourceConfig.WebDavHref(root, current.Directory, Single(entry, "href").Value);
                if (entry.Element(Dav + "status") is { } status && !Success(status.Value)) throw new FormatException();
                var properties = entry.Elements(Dav + "propstat").Where(propstat => Success(Single(propstat, "status").Value))
                    .SelectMany(propstat => Single(propstat, "prop").Elements()).ToArray();
                var resourceTypes = properties.Where(property => property.Name == Dav + "resourcetype").ToArray();
                if (resourceTypes.Length != 1) throw new FormatException();
                var collection = resourceTypes[0].Element(Dav + "collection") is not null;
                if (resourceTypes[0].Elements().Any(element => element.Name != Dav + "collection")) throw new FormatException();
                var canonical = collection ? new Uri(href.AbsoluteUri.TrimEnd('/') + "/") : href;
                if (canonical.AbsolutePath.TrimEnd('/') == current.Directory.AbsolutePath.TrimEnd('/'))
                {
                    if (!collection || selfSeen) throw new FormatException();
                    selfSeen = true;
                    continue;
                }
                var relative = canonical.AbsolutePath.StartsWith(current.Directory.AbsolutePath, StringComparison.Ordinal)
                    ? canonical.AbsolutePath[current.Directory.AbsolutePath.Length..].TrimEnd('/') : throw new FormatException();
                if (relative.Length == 0 || relative.Contains('/')) throw new FormatException();
                budget.Node();
                if (collection)
                {
                    if (current.Depth >= maximumDepth || !visited.Add(canonical.AbsoluteUri)) throw new FormatException();
                    queue.Enqueue((canonical, current.Depth + 1));
                    continue;
                }
                var key = budget.Item(canonical.AbsoluteUri);
                var lengths = properties.Where(property => property.Name == Dav + "getcontentlength").ToArray();
                if (lengths.Length > 1) throw new FormatException();
                long? declaredLength = null;
                if (lengths.Length == 1)
                {
                    if (!long.TryParse(lengths[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0 || length > budget.RemainingBytes) throw new FormatException();
                    declaredLength = length;
                }
                var times = properties.Where(property => property.Name == Dav + "getlastmodified").ToArray();
                if (times.Length > 1) throw new FormatException();
                var time = Timestamp(times.SingleOrDefault()?.Value);
                var content = await FetchContentAsync(canonical, options, budget, ct).ConfigureAwait(false);
                if (declaredLength.HasValue && declaredLength != content.Bytes.LongLength) throw new FormatException();
                var mime = PageMime(canonical, content.Bytes, content.ContentType);
                budget.Items.Add(new(key, Filename(Uri.UnescapeDataString(canonical.Segments.Last()), mime), mime, content.Bytes, time));
            }
            if (!selfSeen) throw new FormatException();
        }
        return new(budget.Items, true);
    }

    private static XElement Single(XElement element, string name) => element.Elements(Dav + name).Single();
    private static bool Success(string status)
    {
        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
        return code is >= 200 and < 300;
    }
}