using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnToPilot.Migration.Blobs;

/// <summary>
/// Minimal-but-sufficient validator for
/// <c>migration/manifests/blob-manifest.schema.json</c>. Implemented in
/// plain .NET so we don't take on a full JSON Schema dependency for one
/// manifest shape.
///
/// <para>What it asserts (mirrors the JSON Schema):</para>
/// <list type="bullet">
///   <item>Top-level <c>version</c> is a non-empty semver string.</item>
///   <item>Top-level <c>sourceDirectory</c>, <c>bucket</c>, and
///   <c>generatedAtUtc</c> are present and non-empty.</item>
///   <item><c>entries</c> is an array.</item>
///   <item>Each entry has the five required fields with the right types
///   and patterns: <c>sourcePath</c> and <c>objectKey</c> match
///   <c>^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}$</c>, <c>size</c> is a
///   non-negative integer, <c>sha256</c> is 64 hex chars, and
///   <c>referenceCount</c> is a positive integer.</item>
///   <item><c>additionalProperties</c> = false at every level.</item>
/// </list>
///
/// <para>This is intentionally a strict subset of draft 2020-12. Task 4's
/// gate only needs to know "the manifest is the shape the migration
/// command produces". A future schema-validator dependency can replace
/// this without changing the manifest's wire format.</para>
/// </summary>
public static class BlobManifestSchemaValidator
{
    private static readonly Regex ShaPattern =
        new("^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}$", RegexOptions.Compiled);

    private static readonly Regex Hex64Pattern =
        new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private static readonly Regex SemverPattern =
        new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedTopLevel = new(StringComparer.Ordinal)
    {
        "version", "sourceDirectory", "bucket", "generatedAtUtc", "entries",
    };

    private static readonly HashSet<string> AllowedEntry = new(StringComparer.Ordinal)
    {
        "sourcePath", "objectKey", "size", "sha256", "referenceCount",
    };

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the manifest
    /// fails any of the asserted invariants. A non-throwing call means
    /// the manifest is shape-compatible with the schema.
    /// </summary>
    /// <param name="schemaPath">
    /// Absolute path of <c>blob-manifest.schema.json</c>. Read so a
    /// missing schema is reported with the file's path (the integration
    /// tests rely on the schema being present at this location).
    /// </param>
    /// <param name="manifestJson">The manifest document to validate.</param>
    public static void AssertValid(string schemaPath, string manifestJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaPath);
        ArgumentNullException.ThrowIfNull(manifestJson);

        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                $"BlobManifestSchemaValidator: schema not found at '{schemaPath}'.",
                schemaPath);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: manifest is not valid JSON: {ex.Message}", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: manifest root must be an object, got '{root.ValueKind}'.");
        }

        AssertAllowedProperties(root, AllowedTopLevel, "manifest root");

        foreach (var required in new[] { "version", "sourceDirectory", "bucket", "generatedAtUtc", "entries" })
        {
            if (!root.TryGetProperty(required, out _))
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: manifest is missing required property '{required}'.");
            }
        }

        var version = ReadString(root, "version");
        if (!SemverPattern.IsMatch(version))
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: 'version' must be semver (^[0-9]+\\.[0-9]+\\.[0-9]+$), got '{version}'.");
        }

        ReadString(root, "sourceDirectory"); // asserts string + non-empty
        ReadString(root, "bucket"); // asserts string + non-empty
        ReadDateTime(root, "generatedAtUtc");

        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: 'entries' must be an array, got '{entries.ValueKind}'.");
        }

        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}] must be an object, got '{entry.ValueKind}'.");
            }

            AssertAllowedProperties(entry, AllowedEntry, $"entries[{index}]");

            foreach (var required in new[] { "sourcePath", "objectKey", "size", "sha256", "referenceCount" })
            {
                if (!entry.TryGetProperty(required, out _))
                {
                    throw new InvalidOperationException(
                        $"BlobManifestSchemaValidator: entries[{index}] is missing required property '{required}'.");
                }
            }

            var sourcePath = ReadString(entry, "sourcePath");
            if (!ShaPattern.IsMatch(sourcePath))
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}].sourcePath must match "
                    + "^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}$, got '{sourcePath}'.");
            }

            var objectKey = ReadString(entry, "objectKey");
            if (!ShaPattern.IsMatch(objectKey))
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}].objectKey must match "
                    + "^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}$, got '{objectKey}'.");
            }

            var size = ReadLong(entry, "size");
            if (size < 0)
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}].size must be >= 0, got {size}.");
            }

            var sha = ReadString(entry, "sha256");
            if (!Hex64Pattern.IsMatch(sha))
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}].sha256 must be 64 hex chars, got '{sha}'.");
            }

            var refCount = ReadInt(entry, "referenceCount");
            if (refCount < 1)
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: entries[{index}].referenceCount must be >= 1, got {refCount}.");
            }

            index++;
        }
    }

    private static void AssertAllowedProperties(JsonElement obj, HashSet<string> allowed, string location)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
            {
                throw new InvalidOperationException(
                    $"BlobManifestSchemaValidator: {location} contains unknown property '{prop.Name}' "
                    + $"(allowed: {string.Join(", ", allowed)}).");
            }
        }
    }

    private static string ReadString(JsonElement obj, string name)
    {
        var prop = obj.GetProperty(name);
        if (prop.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' must be a string, got '{prop.ValueKind}'.");
        }
        var value = prop.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' must be a non-empty string.");
        }
        return value;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        var prop = obj.GetProperty(name);
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt64(out var value))
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' must be an integer, got '{prop.ValueKind}'.");
        }
        return value;
    }

    private static int ReadInt(JsonElement obj, string name)
    {
        var value = ReadLong(obj, name);
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' is out of int range: {value}.");
        }
        return (int)value;
    }

    private static DateTimeOffset ReadDateTime(JsonElement obj, string name)
    {
        var prop = obj.GetProperty(name);
        if (prop.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' must be an ISO-8601 string, got '{prop.ValueKind}'.");
        }
        var raw = prop.GetString() ?? string.Empty;
        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"BlobManifestSchemaValidator: '{name}' is not a valid ISO-8601 datetime: '{raw}'.");
        }
        return parsed;
    }
}
