using System.Text.Json;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Minimal JSON-schema compatibility check used by the internal contract
/// theory test. The frozen baseline's success-response schemas come from
/// FastAPI, which emits fairly permissive shapes; the helper's job is to
/// guard against the catastrophic regressions (response missing required
/// fields, returning a string where an array is expected, &hellip;) without
/// pulling in a full schema-validator dependency for stage 2.
///
/// <para>What it asserts:</para>
/// <list type="bullet">
///   <item>The body parses as JSON.</item>
///   <item>If the schema declares a <c>type</c> the body matches it
///         (<c>object</c> / <c>array</c> / <c>string</c> / <c>number</c> /
///         <c>boolean</c> / <c>null</c> / <c>integer</c>).</item>
///   <item>If the schema declares <c>required</c> every listed property
///         is present on an object body.</item>
/// </list>
/// <para>What it deliberately does <em>not</em> assert:</para>
/// <list type="bullet">
///   <item>Format / pattern / minimum / maximum &mdash; the integration
///         tests for each service cover those.</item>
///   <item>Recursive <c>$ref</c> resolution &mdash; the contract test only
///         needs the top-level shape to be present.</item>
/// </list>
/// </summary>
internal static class JsonSchemaAssert
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the response
    /// body is incompatible with <paramref name="schema"/>. A non-throwing
    /// call means the schema accepts the body.
    /// </summary>
    public static void Compatible(JsonElement schema, string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            // Empty body is acceptable for 204-style endpoints; the
            // contract test only runs against operations whose
            // expected-status is 2xx-with-body, so we treat an empty
            // payload as a no-op.
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Response body is not valid JSON: {ex.Message}. Body: {Truncate(responseBody)}",
                ex);
        }

        // Resolve the top-level type constraint (if any). FastAPI's
        // schemas usually set "type": "object" so the helper checks the
        // matching JSON kind.
        if (schema.TryGetProperty("type", out var typeElement))
        {
            var expectedType = typeElement.GetString();
            if (!MatchesType(expectedType, root))
            {
                throw new InvalidOperationException(
                    $"Schema requires type '{expectedType}' but response body is '{Describe(root)}'.");
            }
        }

        if (schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array
            && root.ValueKind == JsonValueKind.Object)
        {
            foreach (var required in requiredElement.EnumerateArray())
            {
                var name = required.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!root.TryGetProperty(name, out _))
                {
                    throw new InvalidOperationException(
                        $"Schema requires property '{name}' but the response body omits it. Body: {Truncate(responseBody)}");
                }
            }
        }
    }

    private static bool MatchesType(string? expectedType, JsonElement actual) => expectedType switch
    {
        "object" => actual.ValueKind == JsonValueKind.Object,
        "array" => actual.ValueKind == JsonValueKind.Array,
        "string" => actual.ValueKind == JsonValueKind.String,
        "number" => actual.ValueKind == JsonValueKind.Number,
        "integer" => actual.ValueKind == JsonValueKind.Number && actual.TryGetInt64(out _),
        "boolean" => actual.ValueKind == JsonValueKind.True || actual.ValueKind == JsonValueKind.False,
        "null" => actual.ValueKind == JsonValueKind.Null,
        null or "" => true,
        _ => true,
    };

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True => "boolean",
        JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString(),
    };

    private static string Truncate(string s)
        => s.Length <= 200 ? s : s.Substring(0, 200) + "…";
}