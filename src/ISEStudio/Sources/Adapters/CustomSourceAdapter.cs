using System.Text;
using System.Text.Json;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class CustomSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    public override string Kind => SourceKind.Custom;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var origin = HttpSourceConfig.Normalize(config.GetProperty("endpoint").GetString()!, allowQuery: false);
        var current = origin;
        var items = budget.Items;
        while (true)
        {
            budget.Page(current);
            var response = await FetchResponseAsync(current, options, budget, ct).ConfigureAwait(false);
            current = response.FinalUri;
            using var page = JsonDocument.Parse(response.Bytes);
            var root = page.RootElement;
            RequireObject(root);
            var entries = root.GetProperty("items");
            if (entries.ValueKind != JsonValueKind.Array) throw new FormatException();
            foreach (var entry in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                RequireObject(entry);
                var key = budget.Item(entry.GetProperty("id").GetString()!);
                var content = entry.GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new FormatException();
                var title = OptionalString(entry, "title") ?? key;
                var mime = OptionalString(entry, "mime") ?? "text/markdown";
                var timestamp = Timestamp(OptionalString(entry, "doc_time"));
                items.Add(new(key, Filename(title, mime), mime, Encoding.UTF8.GetBytes(content), timestamp));
            }
            var complete = root.TryGetProperty("is_complete", out var flag) && flag.GetBoolean();
            var next = OptionalString(root, "next_url");
            if (next is null) return new(items, complete);
            if (complete || string.IsNullOrWhiteSpace(next)) throw new FormatException();
            current = HttpSourceConfig.Continue(origin, current, next);
        }
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new FormatException();
    }
}