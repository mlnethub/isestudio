using System.Text;
using System.Text.Json;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class NotionSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    private const string Api = "https://api.notion.com/v1/";
    public override string Kind => SourceKind.Notion;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var scope = ContentSourceConfig.Scope(config);
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        if (scope == "page")
        {
            var id = ContentSourceConfig.NotionId(config.GetProperty("page_id").GetString()!);
            var uri = new Uri(Api + "pages/" + id);
            budget.Page(uri);
            using var page = JsonDocument.Parse(await FetchAsync(uri, options, budget, ct).ConfigureAwait(false));
            if (ContentSourceConfig.NotionId(page.RootElement.GetProperty("id").GetString()!) != id) throw new FormatException();
            await AddPage(page.RootElement).ConfigureAwait(false);
        }
        else
        {
            var uri = new Uri(Api + (scope == "database" ? "databases/" + ContentSourceConfig.NotionId(config.GetProperty("database_id").GetString()!) + "/query" : "search"));
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var body = new Dictionary<string, object> { ["page_size"] = 100 };
                if (scope == "search")
                {
                    body["filter"] = new { property = "object", value = "page" };
                    if (config.TryGetProperty("query", out var query)) body["query"] = query.GetString()!;
                }
                if (cursor is not null) body["start_cursor"] = cursor;
                if (!cursors.Add(cursor ?? "")) throw new FormatException();
                budget.RequestPage();
                await using var response = await Http.PostJsonAsync(uri, JsonSerializer.SerializeToUtf8Bytes(body), options, ct).ConfigureAwait(false);
                using var list = JsonDocument.Parse((await ReadContentAsync(response, budget, ct).ConfigureAwait(false)).Bytes);
                foreach (var page in Results(list.RootElement)) await AddPage(page).ConfigureAwait(false);
                cursor = Next(list.RootElement);
            } while (cursor is not null);
        }
        return new(budget.Items, true);

        async Task AddPage(JsonElement page)
        {
            ct.ThrowIfCancellationRequested();
            if (page.GetProperty("object").GetString() != "page") throw new FormatException();
            var id = ContentSourceConfig.NotionId(page.GetProperty("id").GetString()!);
            if (!seenPages.Add(id)) throw new FormatException();
            budget.Node();
            if (Deleted(page)) return;
            var key = budget.Item(id);
            var time = Timestamp(page.GetProperty("last_edited_time").GetString()) ?? throw new FormatException();
            var properties = page.GetProperty("properties");
            if (properties.ValueKind != JsonValueKind.Object) throw new FormatException();
            var titles = properties.EnumerateObject().Select(property => property.Value)
                .Where(property => property.TryGetProperty("type", out var type) && type.GetString() == "title").ToArray();
            if (titles.Length > 1) throw new FormatException();
            var title = titles.Length == 0 ? "Untitled" : RichText(titles[0].GetProperty("title"));
            if (string.IsNullOrWhiteSpace(title)) title = "Untitled";
            var text = new StringBuilder();
            Append(text, "# " + title + "\n\n", budget);
            await Blocks(id, 0, text, new HashSet<string>(StringComparer.Ordinal) { id }).ConfigureAwait(false);
            budget.Items.Add(new(key, Filename(title, "text/markdown"), "text/markdown", Encoding.UTF8.GetBytes(text.ToString()), time));
        }

        async Task Blocks(string parent, int depth, StringBuilder text, HashSet<string> ancestors)
        {
            if (depth > ContentSourceConfig.Depth(config)) throw new FormatException();
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var children = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var uri = new Uri(Api + "blocks/" + parent + "/children?page_size=100" + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor)));
                if (!cursors.Add(cursor ?? "")) throw new FormatException();
                budget.RequestPage();
                using var document = JsonDocument.Parse(await FetchAsync(uri, options, budget, ct).ConfigureAwait(false));
                foreach (var block in Results(document.RootElement))
                {
                    ct.ThrowIfCancellationRequested();
                    budget.Node();
                    if (block.GetProperty("object").GetString() != "block") throw new FormatException();
                    var id = ContentSourceConfig.NotionId(block.GetProperty("id").GetString()!);
                    if (!children.Add(id) || ancestors.Contains(id)) throw new FormatException();
                    if (Deleted(block)) continue;
                    var type = block.GetProperty("type").GetString()!;
                    if (type == "unsupported") throw new FormatException();
                    var value = block.GetProperty(type);
                    var rendered = Render(type, value);
                    if (rendered.Length > 0) Append(text, rendered + "\n", budget);
                    if (block.GetProperty("has_children").GetBoolean())
                    {
                        ancestors.Add(id);
                        try { await Blocks(id, depth + 1, text, ancestors).ConfigureAwait(false); }
                        finally { ancestors.Remove(id); }
                    }
                }
                cursor = Next(document.RootElement);
            } while (cursor is not null);
        }
    }

    private static JsonElement.ArrayEnumerator Results(JsonElement list)
    {
        if (list.GetProperty("object").GetString() != "list") throw new FormatException();
        return list.GetProperty("results").EnumerateArray();
    }

    private static string? Next(JsonElement list)
    {
        var more = list.GetProperty("has_more").GetBoolean();
        var next = list.GetProperty("next_cursor");
        if (!more)
        {
            if (next.ValueKind != JsonValueKind.Null) throw new FormatException();
            return null;
        }
        var cursor = next.GetString();
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 1024 || cursor.Any(char.IsControl)) throw new FormatException();
        return cursor;
    }

    private static bool Deleted(JsonElement item) => new[] { "archived", "is_archived", "in_trash" }
        .Any(field => item.TryGetProperty(field, out var flag) && flag.GetBoolean());

    private static string RichText(JsonElement values)
    {
        var text = new StringBuilder();
        foreach (var value in values.EnumerateArray())
        {
            var segment = value.TryGetProperty("plain_text", out var plain) ? plain.GetString()
                : value.GetProperty("text").GetProperty("content").GetString();
            text.Append(segment ?? throw new FormatException());
        }
        return text.ToString();
    }

    private static string Render(string type, JsonElement value)
    {
        if (type == "table_row") return string.Join(" | ", value.GetProperty("cells").EnumerateArray().Select(RichText));
        if (type == "child_page") return "- " + value.GetProperty("title").GetString();
        if (type == "child_database") return "- " + value.GetProperty("title").GetString();
        if (type == "equation") return value.GetProperty("expression").GetString() ?? throw new FormatException();
        var text = value.TryGetProperty("rich_text", out var rich) ? RichText(rich) : "";
        return type switch
        {
            "heading_1" => "## " + text, "heading_2" => "### " + text, "heading_3" => "#### " + text,
            "bulleted_list_item" => "- " + text, "numbered_list_item" => "1. " + text,
            "to_do" => (value.GetProperty("checked").GetBoolean() ? "- [x] " : "- [ ] ") + text,
            "quote" => "> " + text, "code" => "```\n" + text + "\n```", "divider" => "---",
            _ => text,
        };
    }

    private static void Append(StringBuilder text, string value, ScanBudget budget)
    {
        budget.CountBytes(Encoding.UTF8.GetByteCount(value));
        text.Append(value);
    }
}