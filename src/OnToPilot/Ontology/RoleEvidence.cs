using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OnToPilot.Ontology;

/// <summary>
/// Domain-neutral evidence helpers for TBox/ABox role decisions. Mirrors
/// <c>backend/app/ontology/role_evidence.py</c>. No ontology-domain vocabulary
/// lives here: only generic structured-data invariants and source-grounding
/// checks so an independent role critic can decide semantics.
/// </summary>
public static class RoleEvidence
{
    public const string RoleType = "type";
    public const string RoleIndividual = "individual";
    public const string RoleLiteral = "literal";
    public const string RoleUncertain = "uncertain";

    private static readonly HashSet<string> TypeFieldMarkers = new(StringComparer.Ordinal)
    {
        "category", "class", "kind", "type",
        // Chinese equivalents.
        "类别", "分类", "种类", "类型",
    };

    private static readonly Regex FieldLineRegex = new(
        @"(?m)^\s*(?:[-*]\s+)?(?<key>[^:\n]{1,96})\s*[:：]\s*(?<value>[^\n]+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex JsonPairRegex = new(
        "\"(?<key>[^\"\\\\]{1,96})\"\\s*:\\s*(?<value>\"(?:\\\\.|[^\"\\\\])*\"|[-+]?\\d+(?:\\.\\d+)?|true|false|null)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ShortcodeRegex = new(@"\{\{[<%]\s*(.*?)\s*[>%]\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ShortcodeTextRegex = new(
        @"\btext\s*=\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ListSplitRegex = new(@"\s*(?:,|，|;|；|\||→)\s*", RegexOptions.Compiled);
    private static readonly Regex CjkRegex = new("[㐀-鿿豈-﫿]", RegexOptions.Compiled);

    private static readonly Regex WordRegex = new(@"\w+", RegexOptions.Compiled | RegexOptions.Singleline);

    // ------------------------------------------------------------------
    // Normalization
    // ------------------------------------------------------------------

    /// <summary>
    /// Normalize text for source-grounding comparisons without translating it.
    /// Strips Markdown link URLs, shortcodes, and HTML tags; casefolds;
    /// replaces underscores with spaces; extracts word characters (preserving
    /// non-ASCII letters).
    /// </summary>
    public static string Normalize(string? value)
    {
        value = (value ?? string.Empty).Normalize(NormalizationForm.FormKC);
        value = MarkdownLinkRegex.Replace(value, "$1");
        value = ShortcodeRegex.Replace(value, ShortcodeVisibleText);
        value = HtmlTagRegex.Replace(value, " ");
        value = value.Replace('_', ' ');
        var matches = WordRegex.Matches(value);
        var sb = new StringBuilder();
        bool first = true;
        foreach (Match m in matches)
        {
            if (!first) sb.Append(' ');
            sb.Append(m.Value.ToLowerInvariant());
            first = false;
        }
        return sb.ToString();
    }

    private static string ShortcodeVisibleText(Match match)
    {
        var textMatch = ShortcodeTextRegex.Match(match.Groups[1].Value);
        return textMatch.Success ? " " + textMatch.Groups["value"].Value + " " : " ";
    }

    private static bool NormalizedPhraseIn(string sourceText, string phrase)
    {
        var normalizedSource = Normalize(sourceText);
        var normalizedPhrase = Normalize(phrase);
        if (string.IsNullOrEmpty(normalizedPhrase)) return false;
        if (CjkRegex.IsMatch(normalizedPhrase))
        {
            return normalizedSource.Contains(normalizedPhrase, StringComparison.Ordinal);
        }
        var escaped = Regex.Escape(normalizedPhrase);
        return Regex.IsMatch(normalizedSource, $@"(?:^| ){escaped}(?:$| )");
    }

    // ------------------------------------------------------------------
    // Groundedness / instance declaration checks
    // ------------------------------------------------------------------

    /// <summary>Return whether an asserted evidence span is actually present in the source.</summary>
    public static bool EvidenceIsGrounded(string sourceText, object? evidence, int minChars = 4)
    {
        if (evidence is not string s) return false;
        var normalized = Normalize(s).Replace(" ", "");
        if (normalized.Length < minChars) return false;
        return NormalizedPhraseIn(sourceText, s);
    }

    /// <summary>Require an individual label to occur in the source rather than be model-invented.</summary>
    public static bool SurfaceIsGrounded(string sourceText, object? surface)
    {
        if (surface is not string s || string.IsNullOrWhiteSpace(s)) return false;
        var stripped = s.Trim();
        var escaped = Regex.Escape(stripped);
        if (Regex.IsMatch(sourceText, $@"(?<![\w-]){escaped}(?![\w-])", RegexOptions.IgnoreCase))
        {
            return true;
        }
        return NormalizedPhraseIn(sourceText, stripped);
    }

    /// <summary>
    /// Return whether the source explicitly names <paramref name="label"/> as
    /// an instance / individual. Deliberately narrow: only direct identity
    /// wording with the candidate as the grammatical subject, so a type
    /// label in <c>X is an instance of Pump</c> does not accidentally make
    /// <c>Pump</c> an individual.
    /// </summary>
    public static bool HasExplicitIndividualDeclaration(string sourceText, object? label)
    {
        if (label is not string s || string.IsNullOrWhiteSpace(s)) return false;
        var tokens = WordRegex.Matches(s.Normalize(NormalizationForm.FormKC));
        if (tokens.Count == 0) return false;
        var body = string.Join(@"[\s_-]+", tokens.Cast<Match>().Select(m => Regex.Escape(m.Value)));
        var decorated = $@"[`*_]*{body}[`*_]*";
        var qname = $@"[`*_]*[A-Za-z][\w-]*:{body}[`*_]*";
        var patterns = new[]
        {
            // A definite, explicitly identified QName: "the `ex:Pump_1` instance".
            $@"\bthe\s+{qname}\s+(?:named\s+)?(?:instance|individual)\b",
            // The exact label is the subject of a direct identity assertion.
            $@"(?<![\w-]){decorated}\s+is\s+(?:an?\s+|the\s+)?(?:named\s+)?(?:instance|individual)\b",
            $@"\b(?:instance|individual)\s+(?:named|called)\s+{decorated}(?![\w-])",
            // Chinese: "名为 X 的实例", "该 X 实例", "X 是一个实例". No \b — .NET regex
            // treats CJK ideographs as word characters so a boundary never fires
            // between two adjacent Chinese characters, silently hiding matches.
            $@"(?:名为|称为)\s*{decorated}\s*的?(?:实例|个体)",
            $@"该\s*{decorated}\s*(?:实例|个体)",
            $@"{decorated}\s*是\s*(?:一个|该)?\s*(?:实例|个体)",
        };
        foreach (var p in patterns)
        {
            if (Regex.IsMatch(sourceText, p, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Structured-value role extraction
    // ------------------------------------------------------------------

    private static bool FieldIsTypeDeclaration(string key)
    {
        var normalized = Normalize(key);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        return TypeFieldMarkers.Contains(normalized) || TypeFieldMarkers.Contains(tokens[^1]);
    }

    private static string CleanScalar(string value)
    {
        value = value.Trim().TrimEnd(',').Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' or '`')
        {
            value = value[1..^1];
        }
        return value.Trim();
    }

    private static List<string> ScalarValues(string rawValue)
    {
        var cleaned = CleanScalar(rawValue);
        if (string.IsNullOrEmpty(cleaned) || cleaned is "{}" or "[]" or "|" or ">") return new List<string>();
        var values = new List<string> { cleaned };
        var bracketed = (cleaned.Length >= 2 && cleaned[0] is '[' or '(' && cleaned[^1] is ']' or ')')
            ? cleaned[1..^1].Trim()
            : cleaned;
        if (!bracketed.Contains("://", StringComparison.Ordinal)
            && (bracketed.Contains(',') || bracketed.Contains('，') || bracketed.Contains(';')
                || bracketed.Contains('；') || bracketed.Contains('|') || bracketed.Contains('→')))
        {
            foreach (var part in ListSplitRegex.Split(bracketed))
            {
                var clean = CleanScalar(part);
                if (!string.IsNullOrEmpty(clean)) values.Add(clean);
            }
        }
        // De-duplicate preserving order.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var v in values)
        {
            if (seen.Add(v)) result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// Map exact structured scalar values to generic source roles
    /// (<see cref="RoleType"/> / <see cref="RoleLiteral"/>). Values of explicit
    /// <c>type</c>/<c>kind</c>/<c>class</c>/<c>category</c> fields may denote
    /// reusable types. Every other scalar is merely a value.
    /// </summary>
    public static Dictionary<string, HashSet<string>> StructuredValueRoles(string? sourceText)
    {
        var roles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Add(string key, string rawValue)
        {
            var role = FieldIsTypeDeclaration(key) ? RoleType : RoleLiteral;
            foreach (var value in ScalarValues(rawValue))
            {
                var normalized = Normalize(value);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (!roles.TryGetValue(normalized, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    roles[normalized] = set;
                }
                set.Add(role);
            }
        }

        if (string.IsNullOrEmpty(sourceText)) return roles;
        foreach (Match m in FieldLineRegex.Matches(sourceText))
        {
            Add(m.Groups["key"].Value, m.Groups["value"].Value);
        }
        foreach (Match m in JsonPairRegex.Matches(sourceText))
        {
            Add(m.Groups["key"].Value, m.Groups["value"].Value);
        }
        return roles;
    }

    /// <summary>
    /// Return structured values that have no independent explicit type
    /// declaration. Used by <see cref="TBoxGuard"/> to flag bare named
    /// entities as likely ABox individuals.
    /// </summary>
    public static Dictionary<string, string> StructuredNonTypeValues(string? sourceText)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (value, roles) in StructuredValueRoles(sourceText))
        {
            if (roles.Contains(RoleLiteral) && !roles.Contains(RoleType))
            {
                result[value] = "structured scalar value without an explicit type declaration";
            }
        }
        return result;
    }

    /// <summary>Casefold a label for use in normalization. Provided for symmetry.</summary>
    public static string Casefold(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
}