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
    /// <summary>Key for an ABox individual: <c>ind|&lt;iri&gt;</c>.</summary>
    public static string IndividualKey(string iri) => $"ind|{iri}";

    /// <summary>
    /// Key for a data-property assertion:
    /// <c>data|&lt;subject&gt;|&lt;property&gt;|&lt;value&gt;</c>. Mirrors the
    /// Python <c>data_key</c> format exactly; <paramref name="value"/> is
    /// embedded raw, callers that need collision resistance can pre-hash.
    /// </summary>
    public static string DataKey(string subject, string property, string value) =>
        $"data|{subject}|{property}|{value}";

    /// <summary>Key for an object-property assertion: <c>obj|&lt;sub&gt;|&lt;prop&gt;|&lt;target&gt;</c>.</summary>
    public static string ObjectKey(string subject, string property, string target) =>
        $"obj|{subject}|{property}|{target}";
}