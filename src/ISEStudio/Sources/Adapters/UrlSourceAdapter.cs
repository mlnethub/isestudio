using System.Text;
using System.Text.Json;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class UrlSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    public override string Kind => SourceKind.Url;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var items = budget.Items;
        foreach (var value in config.GetProperty("urls").EnumerateArray())
        {
            var uri = HttpSourceConfig.Normalize(value.GetString()!, allowQuery: false);
            budget.Page(uri);
            var key = budget.Item(uri.AbsoluteUri);
            var response = await FetchResponseAsync(uri, options, budget, ct).ConfigureAwait(false);
            var body = response.Bytes;
            if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(body))) throw new FormatException();
            var mime = PageMime(response.FinalUri, body, response.ContentType);
            items.Add(new(key, Filename(uri.Host + uri.AbsolutePath.Replace('/', '-'), mime), mime, body, null));
        }
        return new(items, true);
    }
}