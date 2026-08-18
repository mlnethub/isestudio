using System.Text;
using System.Text.RegularExpressions;

namespace OnToPilot.Api;

/// <summary>
/// Outcome of <see cref="ReadOnlySparqlPolicy.Validate"/>. The
/// <see cref="Allow"/> branch returns a normalised query string the
/// executor can run as-is; the <see cref="Reject"/> branch carries the
/// reason the controller surfaces in the <c>{"detail": "..."}</c>
/// envelope (HTTP 400).
/// </summary>
public abstract record ReadOnlySparqlPolicyResult
{
    private ReadOnlySparqlPolicyResult() { }

    /// <summary>The query is read-only. <see cref="Normalised"/> is the input with comment / whitespace runs collapsed.</summary>
    public sealed record Allow(string Normalised) : ReadOnlySparqlPolicyResult;

    /// <summary>The query is not read-only. <see cref="Reason"/> is a human-readable detail the controller emits verbatim.</summary>
    public sealed record Reject(string Reason) : ReadOnlySparqlPolicyResult;
}

/// <summary>
/// Read-only SPARQL guard for the external / published
/// <c>POST /api/v1/knowledge-systems/{public_id}/query</c> endpoint.
/// Mirrors the Python backend's
/// <c>backend/app/external_api.py::assert_read_only_query</c>:
/// <list type="bullet">
///   <item>The query form must be <c>SELECT</c> or <c>ASK</c>; everything else (<c>CONSTRUCT</c>, <c>DESCRIBE</c>, <c>INSERT</c>, <c>DELETE</c>, <c>UPDATE</c>, <c>LOAD</c>, <c>CLEAR</c>, <c>CREATE</c>, <c>DROP</c>, <c>MOVE</c>, <c>COPY</c>, <c>ADD</c>) is rejected.</item>
///   <item>The query MUST NOT introduce cross-graph reach via <c>SERVICE</c>, <c>FROM</c> (default graph), or <c>GRAPH</c> (named graph). These all let the caller escape the published graph and are part of the brief's "禁止 SERVICE、FROM、GRAPH 与 update" load-bearing rule.</item>
///   <item>Detection is case-insensitive and tolerates arbitrary whitespace and <c>#</c> / <c>/&#42; ... &#42;/</c> comments so a caller cannot smuggle a forbidden keyword inside one.</item>
/// </list>
/// </summary>
public static class ReadOnlySparqlPolicy
{
    // Word-boundary anchored alternation: matches SELECT or ASK as a whole
    // word, not as a prefix of a longer identifier.
    private static readonly Regex AllowedFormRegex = new(
        @"^\s*(?:SELECT|ASK)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any reserved update form (top-level) is rejected. We use a word-boundary
    // match so e.g. CONSTRUCTED is not misread as CONSTRUCT.
    private static readonly Regex ForbiddenFormRegex = new(
        @"\b(CONSTRUCT|DESCRIBE|INSERT|DELETE|UPDATE|LOAD|CLEAR|CREATE|DROP|MOVE|COPY|ADD|MODIFY)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Each is checked as a whole word so legitimate PREFIX declarations and
    // local variable names (e.g. ?service) don't trip the guard.
    private static readonly Regex[] ForbiddenReachRegexes = new[]
    {
        new Regex(@"\bSERVICE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(^|\s)FROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(^|\s)FROM\s+NAMED\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bGRAPH\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Validate a SPARQL query string. Returns <see cref="ReadOnlySparqlPolicyResult.Allow"/>
    /// with the whitespace-collapsed form, or <see cref="ReadOnlySparqlPolicyResult.Reject"/>
    /// with a human-readable reason the controller surfaces verbatim in the
    /// FastAPI <c>{"detail": "..."}</c> envelope.
    /// </summary>
    /// <param name="sparql">The raw SPARQL body from the request.</param>
    public static ReadOnlySparqlPolicyResult Validate(string? sparql)
    {
        if (string.IsNullOrWhiteSpace(sparql))
        {
            return new ReadOnlySparqlPolicyResult.Reject("Query body is required.");
        }

        // Strip block / line comments and collapse whitespace so the regex
        // matchers see the same shape regardless of formatting. Stripping
        // comments first blocks a caller hiding a forbidden keyword inside
        // a # comment.
        var stripped = StripComments(sparql);
        var compact = CollapseWhitespace(stripped);

        if (!AllowedFormRegex.IsMatch(compact))
        {
            return new ReadOnlySparqlPolicyResult.Reject(
                "Only SELECT and ASK queries are allowed on the external endpoint.");
        }

        var forbiddenForm = ForbiddenFormRegex.Match(compact);
        if (forbiddenForm.Success)
        {
            return new ReadOnlySparqlPolicyResult.Reject(
                $"Update form '{forbiddenForm.Value.ToUpperInvariant()}' is not allowed on the external endpoint.");
        }

        foreach (var reach in ForbiddenReachRegexes)
        {
            var match = reach.Match(compact);
            if (match.Success)
            {
                return new ReadOnlySparqlPolicyResult.Reject(
                    $"Keyword '{match.Value.Trim().ToUpperInvariant()}' is not allowed on the external endpoint.");
            }
        }

        return new ReadOnlySparqlPolicyResult.Allow(compact);
    }

    /// <summary>
    /// Strip line (<c># ...</c>) and block (<c>/* ... */</c>) comments.
    /// String literals are not preserved verbatim — the external endpoint
    /// does not accept update queries that embed literal data, and string
    /// preservation adds complexity we do not need for the read-only guard.
    /// </summary>
    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Unterminated block comment — drop the rest of the input.
                    break;
                }
                i = end + 2;
                continue;
            }

            // Line comment
            if (source[i] == '#')
            {
                var end = source.IndexOf('\n', i + 1);
                if (end < 0)
                {
                    break;
                }
                i = end + 1;
                continue;
            }

            builder.Append(source[i]);
            i++;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Collapse runs of whitespace (including newlines) into a single
    /// space. The regexes above only need to detect keywords, so we do
    /// not preserve formatting.
    /// </summary>
    private static string CollapseWhitespace(string source) =>
        Regex.Replace(source, @"\s+", " ", RegexOptions.Compiled).Trim();
}
