using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources.Adapters;

public sealed class ObjectStorageSourceException(string message) : Exception(message);

public static class ObjectStorageSourceConfig
{
    public static IReadOnlyList<SourceKindDescriptor> Descriptors { get; } =
    [
        new("s3", true,
        [
            new("bucket", "string", true, false), new("prefix", "string", false, false),
            new("region", "string", true, false), new("access_key_id", "string", true, true),
            new("secret_access_key", "string", true, true), new("session_token", "string", false, true),
        ], config => Validate("s3", config)),
        new("gcs", true,
        [
            new("bucket", "string", true, false), new("prefix", "string", false, false),
            new("service_account_key", "string", true, true),
        ], config => Validate("gcs", config)),
    ];

    public static string? Validate(string kind, JsonElement config)
    {
        var descriptor = Descriptors.SingleOrDefault(item => item.Kind == kind);
        if (descriptor is null || config.ValueKind != JsonValueKind.Object)
            return "Object storage config is invalid.";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in config.EnumerateObject())
        {
            if (!names.Add(property.Name) || !descriptor.ConfigFields.Any(field => field.Name == property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
                return "Object storage config contains an invalid field.";
        }
        foreach (var field in descriptor.ConfigFields.Where(field => field.Required))
            if (!config.TryGetProperty(field.Name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
                return "Object storage config is missing a required field.";
        var bucket = config.GetProperty("bucket").GetString()!;
        var maximumBucketLength = kind == "s3" ? 63 : 222;
        if (bucket.Length < 3 || bucket.Length > maximumBucketLength
            || !Regex.IsMatch(bucket, kind == "gcs" ? "^[a-z0-9][a-z0-9._-]*[a-z0-9]$" : "^[a-z0-9][a-z0-9.-]*[a-z0-9]$", RegexOptions.CultureInvariant)
            || bucket.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
            || System.Net.IPAddress.TryParse(bucket, out _)
            || bucket.Contains("..", StringComparison.Ordinal)
            || (kind == "s3" && (bucket.StartsWith("xn--", StringComparison.Ordinal)
                || bucket.StartsWith("sthree-", StringComparison.Ordinal) || bucket.StartsWith("amzn-s3-demo-", StringComparison.Ordinal)
                || new[] { "-s3alias", "--ol-s3", ".mrap", "--x-s3", "--table-s3" }.Any(suffix => bucket.EndsWith(suffix, StringComparison.Ordinal))))
            || (kind == "gcs" && (bucket.StartsWith("goog", StringComparison.Ordinal) || bucket.Contains("google", StringComparison.Ordinal))))
            return "Object storage bucket is invalid.";
        if (config.TryGetProperty("prefix", out var prefix) && !ValidKey(prefix.GetString()!, allowEmpty: true))
            return "Object storage prefix is invalid.";
        if (kind == "s3" && !RegionEndpoint.EnumerableAllRegions.Any(region => region.SystemName == config.GetProperty("region").GetString()))
            return "S3 region must be an official region name.";
        return null;
    }

    public static bool ValidKey(string key, bool allowEmpty = false)
    {
        if ((!allowEmpty && key.Length == 0) || Encoding.UTF8.GetByteCount(key) > 1024
            || key.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(key); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    public static string? ValidateServiceAccount(string json)
    {
        try
        {
            if (json.Length > 65536) return "GCS service account key is invalid.";
            using var document = JsonDocument.Parse(json);
            var account = document.RootElement;
            if (account.ValueKind != JsonValueKind.Object) return "GCS service account key is invalid.";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in account.EnumerateObject())
                if (!names.Add(property.Name)) return "GCS service account key is invalid.";
            if (Text(account, "type") != "service_account" || string.IsNullOrWhiteSpace(Text(account, "private_key"))
                || !Regex.IsMatch(Text(account, "client_email"), "^[a-zA-Z0-9][a-zA-Z0-9._-]*@[a-z0-9][a-z0-9-]*\\.iam\\.gserviceaccount\\.com$", RegexOptions.CultureInvariant)
                || Text(account, "token_uri") != "https://oauth2.googleapis.com/token"
                || (account.TryGetProperty("universe_domain", out _) && Text(account, "universe_domain") != "googleapis.com"))
                return "GCS service account key is invalid.";
            var pem = Text(account, "private_key").AsSpan();
            if (!PemEncoding.TryFind(pem, out var fields) || !pem[fields.Label].SequenceEqual("PRIVATE KEY")
                || !pem[..fields.Location.Start.GetOffset(pem.Length)].Trim().IsEmpty
                || !pem[fields.Location.End.GetOffset(pem.Length)..].Trim().IsEmpty)
                return "GCS service account key is invalid.";
            var bytes = Convert.FromBase64String(pem[fields.Base64Data].ToString());
            try
            {
                using var key = RSA.Create();
                key.ImportPkcs8PrivateKey(bytes, out var consumed);
                if (consumed != bytes.Length || key.KeySize < 2048) return "GCS service account key is invalid.";
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or CryptographicException or FormatException or ArgumentException)
        {
            return "GCS service account key is invalid.";
        }
    }

    internal static string Text(JsonElement config, string field) => config.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    internal static string OpenSecret(ISourceSecretProtector secrets, SourceEntity source, JsonElement config, string field, bool optional = false)
    {
        if (!config.TryGetProperty(field, out var value) && optional) return string.Empty;
        try
        {
            if (!secrets.IsConfigured || value.ValueKind != JsonValueKind.String
                || !value.GetString()!.StartsWith("v1:", StringComparison.Ordinal))
                throw new ObjectStorageSourceException("Object storage credentials are unavailable.");
            var plaintext = secrets.Open(value.GetString()!, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field}");
            if (string.IsNullOrWhiteSpace(plaintext)) throw new ObjectStorageSourceException("Object storage credentials are unavailable.");
            return plaintext;
        }
        catch (Exception)
        {
            throw new ObjectStorageSourceException("Object storage credentials are unavailable.");
        }
    }
}