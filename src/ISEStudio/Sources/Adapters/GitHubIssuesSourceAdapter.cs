using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using ISEStudio.Sources.Networking;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Adapters;

public sealed class GitHubIssuesSourceAdapter(ISafeSourceHttpClient http, SourceService sources, IOptions<SourceNetworkOptions> options)
    : HttpSourceAdapter(http, sources, options)
{
    public override string Kind => SourceKind.GithubIssues;

    protected override async Task<DiscoveryResult> DiscoverCoreAsync(JsonElement config, SourceRequestOptions options,
        bool authenticated, ScanBudget budget, CancellationToken ct)
    {
        var repo = config.GetProperty("repo").GetString()!;
        var items = budget.Items;
        var origin = new Uri($"https://api.github.com/repos/{repo}/issues?state=all&per_page=100&page=1");
        var uri = origin;
        for (var pageNumber = 1; ; pageNumber++)
        {
            budget.Page(uri);
            var response = await FetchResponseAsync(uri, options, budget, ct).ConfigureAwait(false);
            using var page = JsonDocument.Parse(response.Bytes);
            var entries = page.RootElement;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 100) throw new FormatException();
            foreach (var issue in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var key = budget.Item(issue.GetProperty("node_id").GetString()!);
                if (issue.TryGetProperty("pull_request", out _)) continue;
                var title = HttpSourceConfig.Key(issue.GetProperty("title").GetString()!);
                var state = issue.GetProperty("state").GetString();
                if (state is not ("open" or "closed")) throw new FormatException();
                var updated = Timestamp(issue.GetProperty("updated_at").GetString()) ?? throw new FormatException();
                var created = issue.TryGetProperty("created_at", out var creation) ? Timestamp(creation.GetString()) : null;
                var closed = issue.TryGetProperty("closed_at", out var closure) && closure.ValueKind != JsonValueKind.Null ? Timestamp(closure.GetString()) : null;
                var body = issue.TryGetProperty("body", out var description) && description.ValueKind != JsonValueKind.Null ? description.GetString() : null;
                var number = issue.GetProperty("number").GetInt64();
                if (number <= 0) throw new FormatException();
                var text = $"# {repo} #{number}: {title}\n\nState: {state}\nCreated: {created:O}\nUpdated: {updated:O}\nClosed: {closed:O}\n\n{body}\n";
                var bytes = Encoding.UTF8.GetBytes(text);
                budget.CountBytes(bytes.Length);
                items.Add(new(key, Filename($"{number}-{title}", "text/markdown"), "text/markdown", bytes, updated));
            }
            if (response.HasMetadata)
            {
                var next = NextPage(origin, response.FinalUri, response.LinkHeader);
                if (next is null) return new(items, true);
                uri = next;
            }
            else
            {
                if (entries.GetArrayLength() < 100) return new(items, true);
                uri = new Uri($"https://api.github.com/repos/{repo}/issues?state=all&per_page=100&page={pageNumber + 1}");
            }
        }
    }

    private static Uri? NextPage(Uri origin, Uri current, string? link)
    {
        if (link is null) return null;
        Uri? next = null;
        foreach (var part in Regex.Split(link, @",(?=\s*<)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var match = Regex.Match(part, @"^\s*<([^<>]+)>\s*(;.*)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new FormatException();
            string? relation = null;
            foreach (var parameter in match.Groups[2].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Trim().Split('=', 2);
                if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0])) throw new FormatException();
                var value = pair[1].Trim();
                if (value.StartsWith('"'))
                {
                    if (value.Length < 2 || !value.EndsWith('"')) throw new FormatException();
                    value = value[1..^1];
                }
                else if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) throw new FormatException();
                if (pair[0].Equals("rel", StringComparison.OrdinalIgnoreCase))
                {
                    if (relation is not null) throw new FormatException();
                    relation = value;
                }
            }
            if (relation is null) throw new FormatException();
            if (!relation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next", StringComparer.OrdinalIgnoreCase)) continue;
            if (next is not null) throw new FormatException();
            var raw = match.Groups[1].Value;
            if (raw.Contains('@') || !Uri.TryCreate(current, raw, out var target)) throw new FormatException();
            target = HttpSourceConfig.Normalize(target.AbsoluteUri, allowQuery: true, allowFragment: false);
            if (target.Scheme != origin.Scheme || target.IdnHost != origin.IdnHost || target.Port != origin.Port
                || target.AbsolutePath != origin.AbsolutePath) throw new FormatException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            int? page = null;
            foreach (var parameter in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length != 2 || !names.Add(pair[0])) throw new FormatException();
                if (pair[0] == "page" && int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) page = number;
                else if (pair[0] == "state" && pair[1] == "all" || pair[0] == "per_page" && pair[1] == "100") { }
                else throw new FormatException();
            }
            if (page is null) throw new FormatException();
            next = new UriBuilder(origin) { Query = $"state=all&per_page=100&page={page.Value}" }.Uri;
        }
        return next;
    }
}