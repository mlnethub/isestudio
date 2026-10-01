using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ISEStudio.Sources.Adapters;

internal static class HttpSourceConfig
{
    public static IReadOnlyList<SourceKindDescriptor> Descriptors { get; } =
    [
        new(SourceKind.GithubIssues, true,
            [new("repo", "string", true, false, "GitHub owner/name repository."),
             new("auth_header", "string", false, true, "Sealed Authorization value. Same-origin HTTPS only."),
             new("max_pages", "integer", false, false, "Maximum pages, 1..100. Exceeding the budget fails the scan.")],
            config => Validate(SourceKind.GithubIssues, config), "Complete issue snapshot, including open and closed issues; excludes pull requests. Identity is node_id."),
        new(SourceKind.JiraIssues, true,
            [new("base_url", "string", true, false, "Jira HTTP(S) site URL without userinfo, query or fragment."),
             new("project", "string", true, false, "Jira project key: ASCII letters, digits and underscore."),
             new("deployment", "string", false, false, "cloud or data_center (default data_center). Cloud uses enhanced v3 search and ADF descriptions."),
             new("auth_header", "string", false, true, "Sealed Authorization value. Same-origin HTTPS only."),
             new("max_pages", "integer", false, false, "Maximum pages, 1..100. Exceeding the budget fails the scan.")],
            config => Validate(SourceKind.JiraIssues, config), "Complete issue snapshot: Data Center /rest/api/2/search; Cloud /rest/api/3/search/jql. Identity is immutable issue id, not display key."),
        new(SourceKind.Url, true,
            [new("urls", "array", true, false, "Nonempty HTTP(S) URL array, without userinfo, query or fragment; at most 100 pages per scan."),
             new("auth_header", "string", false, true, "Sealed Authorization value, e.g. Bearer token. HTTPS only; null or empty explicitly clears it.")],
            config => Validate(SourceKind.Url, config), "GET each configured URL. Stable identity is the normalized configured URL."),
        new(SourceKind.Rss, true,
            [new("feed_url", "string", true, false, "RSS 2.0 or Atom feed URL without userinfo, query or fragment."),
             new("auth_header", "string", false, true, "Sealed Authorization value; sent only to same-origin HTTPS feed pages and articles."),
             new("max_pages", "integer", false, false, "Maximum feed pages, 1..100 (default 100). All feed and article bytes share the scan budget.")],
            config => Validate(SourceKind.Rss, config), "Fetch every article body through the safe HTTP client. No summary fallback. RSS GUID/Atom id, otherwise normalized article link."),
        new(SourceKind.Custom, true,
            [new("endpoint", "string", true, false, "GET endpoint without userinfo, query or fragment."),
             new("auth_header", "string", false, true, "Sealed Authorization value, e.g. Bearer token. HTTPS only; omitted PATCH retains it."),
             new("max_pages", "integer", false, false, "Maximum response pages, 1..100 (default 100). Continuations allow only numeric page/offset/limit queries.")],
            config => Validate(SourceKind.Custom, config), "JSON: items[{id:string,title?:string,content:string,doc_time?:timestamp,mime?:string}]. Plain id is the key. next_url is a same-origin continuation; final is_complete:true declares a full snapshot. Missing/false completion disables reconciliation. No since or deleted/tombstone semantics."),
    ];

    public static string? Validate(string kind, JsonElement config, bool allowFragment = false)
    {
        try
        {
            if (config.ValueKind != JsonValueKind.Object) return "Source config must be an object.";
            var descriptor = Descriptors.Single(item => item.Kind == kind);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in config.EnumerateObject())
            {
                if (!names.Add(property.Name) || !descriptor.ConfigFields.Any(field => field.Name == property.Name))
                    return "Source config contains an unsupported or duplicate field.";
            }
            IEnumerable<Uri> targets;
            if (kind == SourceKind.GithubIssues)
            {
                var repo = config.GetProperty("repo").GetString();
                var parts = repo?.Split('/');
                if (parts is not { Length: 2 } || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                    || part.Length > 100 || !part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                    return "repo must be a GitHub owner/name.";
                targets = [new Uri("https://api.github.com/")];
            }
            else if (kind == SourceKind.Url)
            {
                if (!config.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array || urls.GetArrayLength() == 0)
                    return "Source config requires a nonempty urls array.";
                targets = urls.EnumerateArray().Select(value => ParseUrl(value, allowFragment)).ToArray();
                if (targets.Select(uri => uri.AbsoluteUri).Distinct(StringComparer.Ordinal).Count() != urls.GetArrayLength())
                    return "Source config contains duplicate URLs.";
            }
            else
            {
                var field = kind == SourceKind.Rss ? "feed_url" : kind == SourceKind.JiraIssues ? "base_url" : "endpoint";
                if (!config.TryGetProperty(field, out var value)) return "Source URL config is required.";
                targets = [ParseUrl(value, allowFragment)];
                if (kind == SourceKind.JiraIssues)
                {
                    if (config.TryGetProperty("deployment", out var deployment)
                        && (deployment.ValueKind != JsonValueKind.String || deployment.GetString() is not ("cloud" or "data_center")))
                        return "deployment must be cloud or data_center.";
                    var project = config.GetProperty("project").GetString();
                    if (string.IsNullOrWhiteSpace(project) || project.Length > 255
                        || !project.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                        return "project must be a Jira project key.";
                }
            }
            if (config.TryGetProperty("max_pages", out var pages)
                && (!pages.TryGetInt32(out var maximum) || maximum is < 1 or > 100))
                return "max_pages must be an integer between 1 and 100.";
            if (config.TryGetProperty("auth_header", out var auth))
            {
                if (auth.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(auth.GetString()))
                    return "Source authentication config is invalid.";
                if (targets.Any(uri => uri.Scheme != Uri.UriSchemeHttps))
                    return "Source credentials require HTTPS.";
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return "Source URL config is invalid or contains credentials.";
        }
    }

    private static Uri ParseUrl(JsonElement value, bool allowFragment)
    {
        if (value.ValueKind != JsonValueKind.String) throw new FormatException();
        return Normalize(value.GetString()!, allowQuery: false, allowFragment);
    }

    public static Uri Normalize(string raw, bool allowQuery, bool allowFragment = true)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192 || raw.Any(char.IsControl)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || (!allowQuery && !string.IsNullOrEmpty(uri.Query))
            || (!allowFragment && !string.IsNullOrEmpty(uri.Fragment)))
            throw new FormatException();
        var authority = raw.Trim().AsSpan(raw.Trim().IndexOf("://", StringComparison.Ordinal) + 3);
        var end = authority.IndexOfAny('/', '?', '#');
        if ((end < 0 ? authority : authority[..end]).Contains('@')) throw new FormatException();
        return new UriBuilder(uri) { Scheme = uri.Scheme.ToLowerInvariant(), Host = uri.IdnHost.ToLowerInvariant(), Fragment = "" }.Uri;
    }

    public static Uri Continue(Uri origin, Uri current, string raw)
    {
        if (!Uri.TryCreate(current, raw, out var target)) throw new FormatException();
        target = Normalize(target.AbsoluteUri, allowQuery: true, allowFragment: false);
        if (origin.Scheme != target.Scheme || origin.IdnHost != target.IdnHost || origin.Port != target.Port)
            throw new FormatException();
        foreach (var pair in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[0] is not ("page" or "offset" or "limit")
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
                throw new FormatException();
        }
        return target;
    }

    public static string Key(string value)
    {
        if (value is null) throw new FormatException();
        var key = value.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Any(char.IsControl))
            throw new FormatException();
        return key;
    }
}