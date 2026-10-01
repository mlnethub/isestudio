using System.Globalization;
using System.Text;
using System.Text.Json;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class JiraIssuesSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    public override string Kind => SourceKind.JiraIssues;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var origin = HttpSourceConfig.Normalize(config.GetProperty("base_url").GetString()!, allowQuery: false, allowFragment: false);
        var cloud = config.TryGetProperty("deployment", out var deployment) && deployment.GetString() == "cloud";
        var endpoint = origin.AbsoluteUri.TrimEnd('/') + (cloud ? "/rest/api/3/search/jql" : "/rest/api/2/search");
        var jql = Uri.EscapeDataString($"project = {config.GetProperty("project").GetString()} ORDER BY id ASC");
        var items = budget.Items;
        var offset = 0;
        int? expectedTotal = null;
        string? nextPageToken = null;
        while (true)
        {
            var pagination = cloud ? nextPageToken is null ? "" : "&nextPageToken=" + Uri.EscapeDataString(nextPageToken)
                : "&startAt=" + offset.ToString(CultureInfo.InvariantCulture);
            var uri = new Uri($"{endpoint}?jql={jql}&fields=summary,description,status,created,updated,resolutiondate&maxResults=100{pagination}");
            budget.Page(uri);
            using var page = JsonDocument.Parse(await FetchAsync(uri, options, budget, ct).ConfigureAwait(false));
            var root = page.RootElement;
            var entries = root.GetProperty("issues");
            int total = 0;
            var isLast = false;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 100) throw new FormatException();
            if (cloud)
            {
                isLast = root.GetProperty("isLast").GetBoolean();
                nextPageToken = root.TryGetProperty("nextPageToken", out var token) && token.ValueKind != JsonValueKind.Null ? token.GetString() : null;
                if (!isLast && (string.IsNullOrWhiteSpace(nextPageToken) || nextPageToken.Length > 2048 || nextPageToken.Any(char.IsControl)))
                    throw new FormatException();
            }
            else
            {
                total = root.GetProperty("total").GetInt32();
                var maximum = root.GetProperty("maxResults").GetInt32();
                if (root.GetProperty("startAt").GetInt32() != offset || total < 0 || maximum is < 1 or > 100
                    || entries.GetArrayLength() > maximum || (expectedTotal.HasValue && expectedTotal != total)
                    || entries.GetArrayLength() > total - offset) throw new FormatException();
                expectedTotal = total;
            }
            foreach (var issue in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var identity = issue.GetProperty("id");
                var raw = identity.ValueKind == JsonValueKind.String ? identity.GetString()!
                    : identity.ValueKind == JsonValueKind.Number && identity.TryGetInt64(out var numeric) && numeric > 0
                        ? numeric.ToString(CultureInfo.InvariantCulture) : throw new FormatException();
                var key = budget.Item(raw);
                var fields = issue.GetProperty("fields");
                var title = HttpSourceConfig.Key(fields.GetProperty("summary").GetString()!);
                var status = HttpSourceConfig.Key(fields.GetProperty("status").GetProperty("name").GetString()!);
                var updated = JiraTimestamp(fields.GetProperty("updated").GetString()) ?? throw new FormatException();
                var created = fields.TryGetProperty("created", out var creation) ? JiraTimestamp(creation.GetString()) : null;
                var resolved = fields.TryGetProperty("resolutiondate", out var resolution) && resolution.ValueKind != JsonValueKind.Null ? JiraTimestamp(resolution.GetString()) : null;
                var body = fields.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.Null
                    ? cloud && description.ValueKind == JsonValueKind.Object ? RenderAdf(description, budget) : description.GetString() : null;
                var display = issue.TryGetProperty("key", out var displayKey) ? displayKey.GetString() : key;
                var text = $"# {display}: {title}\n\nStatus: {status}\nCreated: {created:O}\nUpdated: {updated:O}\nResolved: {resolved:O}\n\n{body}\n";
                var bytes = Encoding.UTF8.GetBytes(text);
                budget.CountBytes(bytes.Length);
                items.Add(new(key, Filename($"{display}-{title}", "text/markdown"), "text/markdown", bytes, updated));
            }
            if (cloud)
            {
                if (isLast) return new(items, true);
                continue;
            }
            offset += entries.GetArrayLength();
            if (offset == total) return new(items, true);
            if (entries.GetArrayLength() == 0) throw new FormatException();
        }
    }

    private static string RenderAdf(JsonElement document, ScanBudget budget)
    {
        if (document.GetProperty("type").GetString() != "doc" || document.GetProperty("version").GetInt32() != 1)
            throw new FormatException();
        return Render(document, 0).Trim();

        string Render(JsonElement node, int depth)
        {
            budget.Node();
            if (depth > 32 || node.ValueKind != JsonValueKind.Object) throw new FormatException();
            var type = node.GetProperty("type").GetString();
            if (type == "text")
            {
                var text = node.GetProperty("text").GetString() ?? throw new FormatException();
                if (node.TryGetProperty("marks", out var marks))
                {
                    foreach (var mark in marks.EnumerateArray())
                    {
                        budget.Node();
                        text = mark.GetProperty("type").GetString() switch
                        {
                            "strong" => "**" + text + "**", "em" => "*" + text + "*", "code" => "`" + text + "`",
                            "strike" => "~~" + text + "~~",
                            "link" => "[" + text + "](" + HttpSourceConfig.Normalize(mark.GetProperty("attrs").GetProperty("href").GetString()!, allowQuery: true).AbsoluteUri + ")",
                            "underline" or "textColor" or "backgroundColor" or "subsup" => text,
                            _ => throw new FormatException(),
                        };
                    }
                }
                return text;
            }
            if (type == "hardBreak") return "\n";
            if (type == "rule") return "\n---\n";
            if (type == "mention") return node.GetProperty("attrs").GetProperty("text").GetString() ?? throw new FormatException();
            if (type == "emoji")
            {
                var attrs = node.GetProperty("attrs");
                return (attrs.TryGetProperty("text", out var text) ? text : attrs.GetProperty("shortName")).GetString() ?? throw new FormatException();
            }
            if (type is "inlineCard" or "blockCard")
                return HttpSourceConfig.Normalize(node.GetProperty("attrs").GetProperty("url").GetString()!, allowQuery: true).AbsoluteUri;
            var content = node.TryGetProperty("content", out var children)
                ? string.Concat(children.EnumerateArray().Select(child => Render(child, depth + 1))) : "";
            return type switch
            {
                "doc" or "bulletList" or "orderedList" or "table" or "tableRow" or "panel" or "expand" or "nestedExpand" => content,
                "paragraph" => content + "\n\n",
                "heading" => Heading(node) + content + "\n\n",
                "listItem" => "- " + content.Trim() + "\n",
                "blockquote" => "> " + content.Trim().Replace("\n", "\n> ", StringComparison.Ordinal) + "\n\n",
                "codeBlock" => "```\n" + content + "\n```\n\n",
                "tableCell" or "tableHeader" => content.Trim() + "\t",
                "mediaSingle" or "mediaGroup" => content,
                "media" => node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("alt", out var alt) ? alt.GetString() ?? "" : "",
                _ => throw new FormatException(),
            };
        }

        static string Heading(JsonElement node)
        {
            var level = node.GetProperty("attrs").GetProperty("level").GetInt32();
            if (level is < 1 or > 6) throw new FormatException();
            return new string('#', level) + " ";
        }
    }

    private static DateTimeOffset? JiraTimestamp(string? value)
    {
        if (value is { Length: >= 5 } && value[^5] is '+' or '-'
            && value.AsSpan(value.Length - 4).ToString().All(char.IsAsciiDigit))
            value = value.Insert(value.Length - 2, ":");
        return Timestamp(value);
    }
}