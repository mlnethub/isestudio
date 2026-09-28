using System.Text;

namespace ISEStudio.Ontology;

/// <summary>
/// Deterministic canonical keys for ABox facts. Mirrors the Python
/// <c>abox_provenance.ind_key</c> / <c>data_key</c> / <c>obj_key</c> trio so
/// extraction writes and read-time provenance lookups hash to the same
/// string. Used by the extraction agent, the ABox API, and the audit /
/// review pipeline.
/// </summary>
public static class FactKey
{
    private const int MaxLength = 1024;

    /// <summary>Key for an ABox individual: <c>ind|&lt;iri&gt;</c>.</summary>
    public static string IndividualKey(string iri) => Bound("ind", iri);

    /// <summary>
    /// Key for a data-property assertion:
    /// <c>data|&lt;subject&gt;|&lt;property&gt;|&lt;value&gt;</c>. Mirrors the
    /// Python <c>data_key</c> format exactly; <paramref name="value"/> is
    /// embedded raw while the key fits the database limit; longer keys use a
    /// deterministic SHA-256 representation.
    /// </summary>
    public static string DataKey(string subject, string property, string value) =>
        Bound("data", $"{subject}|{property}|{value}");

    /// <summary>Key for an object-property assertion: <c>obj|&lt;sub&gt;|&lt;prop&gt;|&lt;target&gt;</c>.</summary>
    public static string ObjectKey(string subject, string property, string target) =>
        Bound("obj", $"{subject}|{property}|{target}");

    private static string Bound(string kind, string body)
    {
        var key = $"{kind}|{body}";
        if (key.Length <= MaxLength) return key;

        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        return $"{kind}|sha256|{digest}";
    }
}