using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnToPilot.ApiContract.Tests.Differential;

/// <summary>
/// Recursive JSON walker used by the differential contract runner
/// (<c>migration/scripts/Invoke-ContractComparison.ps1</c>). The runner
/// fires the same scenario at the Python and .NET backends and must
/// ignore the dynamic fields that legitimately differ run-to-run
/// (timestamps, trace ids, opaque access tokens). The normaliser's job
/// is to strip exactly those fields so the structural diff only flags
/// real regressions.
///
/// <para>The contract enforced by <see cref="DifferentialContractTests"/>
/// is:</para>
/// <list type="bullet">
///   <item>Business fields are preserved verbatim &mdash; the normaliser
///         never deletes a property whose name is not on the allowlist.</item>
///   <item>The allowlist is recursive: it applies at every depth, including
///         array entries.</item>
///   <item>Patterns are supported via a trailing <c>*</c> wildcard
///         (e.g. <c>*_token</c> matches <c>access_token</c>,
///         <c>refresh_token</c>, <c>trace_token</c>, &hellip;).</item>
///   <item>Allowlist syntax is a comma-separated list of literal keys and
///         <c>*</c>-suffixed patterns, matching the format documented in
///         <c>migration/contracts/normalization.json</c>.</item>
/// </list>
///
/// <para>Body normalisation is intentionally not destructive on the source
/// payload: the runner feeds the runner's captured response bodies
/// (strings) into <see cref="Apply(string, string)"/>, which parses,
/// strips, and returns a freshly-cloned <see cref="JsonElement"/> the
/// runner can serialise and compare. The runner never mutates the
/// captured response.</para>
/// </summary>
public static class Normalizer
{
    /// <summary>
    /// Parse <paramref name="body"/> as JSON and strip every property
    /// whose name matches one of the literal entries or wildcard patterns
    /// in <paramref name="allowlist"/>. Returns a detached
    /// <see cref="JsonElement"/> safe to serialise and diff.
    /// </summary>
    /// <param name="body">Raw JSON text from the captured response.</param>
    /// <param name="allowlist">Comma-separated list of literal keys and
    /// <c>*</c>-suffixed patterns (e.g. <c>created_at,updated_at,*_token</c>).
    /// Whitespace around entries is trimmed; empty entries are ignored.</param>
    public static JsonElement Apply(string body, string allowlist)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allowlist);

        var patterns = CompileAllowlist(allowlist);
        using var document = JsonDocument.Parse(body);

        // We always rewrite the document so the caller receives a
        // detached JsonElement; JsonDocument.RootElement is invalidated
        // when the document is disposed, and the runner may keep the
        // element alive after Parse returns.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteStripped(writer, document.RootElement, patterns);
        }

        var result = JsonDocument.Parse(new MemoryStream(buffer.WrittenSpan.ToArray()));
        return result.RootElement.Clone();
    }

    private static IReadOnlyList<Regex> CompileAllowlist(string allowlist)
    {
        var entries = allowlist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var compiled = new List<Regex>(entries.Length);
        foreach (var entry in entries)
        {
            // Anchor the pattern so `token` does not also match
            // `access_token`. Wildcards anchor on the left: `*_token`
            // becomes ^.*_token$.
            var anchored = "^" + Regex.Escape(entry).Replace("\\*", ".*") + "$";
            compiled.Add(new Regex(
                anchored,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled));
        }
        return compiled;
    }

    private static void WriteStripped(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyList<Regex> patterns)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsAllowed(property.Name, patterns)) continue;
                    writer.WritePropertyName(property.Name);
                    WriteStripped(writer, property.Value, patterns);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteStripped(writer, item, patterns);
                }
                writer.WriteEndArray();
                break;

            default:
                // Scalars (string/number/bool/null) are written as-is;
                // the allowlist only ever matches property names on
                // objects, never scalar values.
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsAllowed(string propertyName, IReadOnlyList<Regex> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].IsMatch(propertyName)) return true;
        }
        return false;
    }
}
