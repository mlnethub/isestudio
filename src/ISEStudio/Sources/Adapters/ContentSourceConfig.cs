using System.Text.Json;

namespace ISEStudio.Sources.Adapters;

internal static class ContentSourceConfig
{
    public const string NotionVersion = "2022-06-28";
    public static IReadOnlyList<SourceKindDescriptor> Descriptors { get; } =
    [
        new(SourceKind.WebDav, true,
            [new("base_url", "string", true, false, "HTTP(S) WebDAV base URL without credentials, query, fragment or escaped/traversing paths."),
             new("path", "string", true, false, "Root directory path below base_url, starting with /. No escapes or traversal."),
             new("username", "string", false, true, "Sealed Basic username. Requires password and HTTPS."),
             new("password", "string", false, true, "Sealed Basic password. Omitted PATCH retains credentials."),
             new("max_pages", "integer", false, false, "Maximum directory listings, 1..100; default 100."),
             new("max_depth", "integer", false, false, "Maximum directory nesting, 0..16; default 8. Exceeding limits fails, never truncates.")],
            config => Validate(SourceKind.WebDav, config), "Depth:1 PROPFIND and safe GET; canonical origin+href identity. All response and file bytes share the scan budget."),
        new(SourceKind.Notion, true,
            [new("token", "string", true, true, "Sealed integration token. Required for every request."),
             new("scope", "string", false, false, "search (default), page, or database."),
             new("query", "string", false, false, "Optional search title query; search scope only, at most 256 characters."),
             new("page_id", "string", false, false, "Notion UUID for page scope."),
             new("database_id", "string", false, false, "Notion UUID for database scope."),
             new("max_pages", "integer", false, false, "Maximum API pages including block pagination, 1..100; default 100."),
             new("max_depth", "integer", false, false, "Maximum block nesting, 0..16; default 8. All blocks share the item budget.")],
            config => Validate(SourceKind.Notion, config), "Only https://api.notion.com/v1, Notion-Version 2022-06-28. Search/database POST and recursive block GET. Stable page UUID, full snapshots only."),
    ];

    public static string? Validate(string kind, JsonElement config)
    {
        try
        {
            if (config.ValueKind != JsonValueKind.Object) throw new FormatException();
            var descriptor = Descriptors.Single(item => item.Kind == kind);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in config.EnumerateObject())
                if (!names.Add(property.Name) || !descriptor.ConfigFields.Any(field => field.Name == property.Name)) throw new FormatException();
            foreach (var field in descriptor.ConfigFields)
            {
                if (!config.TryGetProperty(field.Name, out var value))
                {
                    if (field.Required) throw new FormatException();
                    continue;
                }
                if (field.Type == "string" && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))) throw new FormatException();
            }
            if (config.TryGetProperty("max_pages", out var pages) && (!pages.TryGetInt32(out var count) || count is < 1 or > 100)) throw new FormatException();
            if (config.TryGetProperty("max_depth", out var depth) && (!depth.TryGetInt32(out var depthCount) || depthCount is < 0 or > 16)) throw new FormatException();
            if (kind == SourceKind.WebDav)
            {
                var root = WebDavRoot(config);
                var username = config.TryGetProperty("username", out _);
                if (username != config.TryGetProperty("password", out _) || (username && root.Scheme != "https")) throw new FormatException();
            }
            else
            {
                var scope = Scope(config);
                if (scope is not ("search" or "page" or "database")) throw new FormatException();
                if (config.TryGetProperty("query", out var query) && (scope != "search" || query.GetString()!.Length > 256 || query.GetString()!.Any(char.IsControl))) throw new FormatException();
                foreach (var field in new[] { "page_id", "database_id" })
                {
                    var expected = scope == (field == "page_id" ? "page" : "database");
                    if (config.TryGetProperty(field, out var id))
                    {
                        if (!expected) throw new FormatException();
                        _ = NotionId(id.GetString()!);
                    }
                    else if (expected) throw new FormatException();
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return "Source content config is invalid or contains unsafe URLs.";
        }
    }

    public static string Scope(JsonElement config) => config.TryGetProperty("scope", out var scope) ? scope.GetString()! : "search";
    public static int Depth(JsonElement config) => config.TryGetProperty("max_depth", out var depth) ? depth.GetInt32() : 8;
    public static string NotionId(string raw)
    {
        if (raw.Length is not (32 or 36) || !Guid.TryParseExact(raw, raw.Length == 32 ? "N" : "D", out var id) || id == Guid.Empty) throw new FormatException();
        return id.ToString("D");
    }

    public static Uri WebDavRoot(JsonElement config)
    {
        var raw = config.GetProperty("base_url").GetString()!;
        ValidatePathText(raw);
        var origin = CanonicalPath(HttpSourceConfig.Normalize(raw, allowQuery: false, allowFragment: false));
        var path = config.GetProperty("path").GetString()!;
        ValidatePathText(path);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) throw new FormatException();
        return CanonicalPath(new Uri(origin.AbsoluteUri.TrimEnd('/') + path.TrimEnd('/') + "/"));
    }

    public static Uri WebDavHref(Uri root, Uri directory, string raw)
    {
        ValidatePathText(raw);
        if (!Uri.TryCreate(directory, raw, out var resolved)) throw new FormatException();
        var target = CanonicalPath(HttpSourceConfig.Normalize(resolved.AbsoluteUri, allowQuery: false, allowFragment: false));
        if (root.Scheme != target.Scheme || root.IdnHost != target.IdnHost || root.Port != target.Port
            || (target.AbsolutePath.TrimEnd('/') != root.AbsolutePath.TrimEnd('/') && !target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))) throw new FormatException();
        return target;
    }

    private static void ValidatePathText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192 || raw.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || raw.IndexOfAny(['\\', '?', '#', '@']) >= 0) throw new FormatException();
        var path = raw;
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authority = raw[(scheme + 3)..];
            var separator = authority.IndexOf('/');
            if ((separator < 0 ? authority : authority[..separator]).Contains('%')) throw new FormatException();
            path = separator < 0 ? "" : authority[separator..];
        }
        if (path.Contains("//", StringComparison.Ordinal)) throw new FormatException();
        foreach (var segment in path.Split('/'))
        {
            for (var index = 0; index < segment.Length; index++)
            {
                if (segment[index] != '%') continue;
                if (index + 2 >= segment.Length || !Uri.IsHexDigit(segment[index + 1]) || !Uri.IsHexDigit(segment[index + 2])) throw new FormatException();
                index += 2;
            }
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Any(char.IsControl) || decoded.IndexOfAny(['/', '\\', '%', '?', '#', '@']) >= 0)
                throw new FormatException();
        }
    }

    private static Uri CanonicalPath(Uri uri)
        => new UriBuilder(uri) { Path = string.Join('/', uri.AbsolutePath.Split('/').Select(Uri.UnescapeDataString)) }.Uri;
}