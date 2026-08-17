using System.Security.Cryptography;
using System.Text;

namespace OnToPilot.Ontology;

/// <summary>
/// Helpers for statement-level provenance created outside model extraction.
/// Mirrors the Python <c>backend/app/ontology/statement_provenance.py</c>:
/// computes stable triple-level keys from the canonical N-Triples dump of
/// a triple, and exposes the helper rules used by the audit-event pipeline.
/// </summary>
/// <remarks>
/// The actual SQL persistence uses the
/// <c>AxiomProvenanceEntity</c> / <c>AboxProvenanceEntity</c> rows in
/// <c>Infrastructure/Persistence/Entities/ProvenanceEntities.cs</c>. This
/// service is the RDF-only side that mints the canonical keys those rows
/// store.
/// </remarks>
public static class StatementProvenanceService
{
    /// <summary>
    /// SHA-256 of the canonical N-Triples serialization of one triple,
    /// prefixed by <c>triple|</c>. Used as the canonical key for an
    /// axiom-level provenance row.
    /// </summary>
    public static string TripleKey(StatementTriple triple)
    {
        ArgumentNullException.ThrowIfNull(triple);
        var ntriples = StatementSerializer.Dump(triple);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ntriples));
        return "triple|" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Build an ABox assertion key from its parts. Mirrors
    /// <c>assertion_key(subject, prop, kind, target, value)</c> in the Python
    /// module.
    /// </summary>
    public static string AssertionKey(string subject, string property, string kind, string? target, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        return kind == "object"
            ? FactKey.ObjectKey(subject, property, target ?? string.Empty)
            : FactKey.DataKey(subject, property, value ?? string.Empty);
    }
}

/// <summary>
/// A neutral triple DTO used by <see cref="StatementProvenanceService"/> so
/// the service is decoupled from the Oxigraph-specific term types.
/// </summary>
public sealed record StatementTriple(string Subject, string Predicate, string Object);

/// <summary>
/// Dump one <see cref="StatementTriple"/> as a single-line N-Triples string.
/// Blank-node and language/datatype escape rules mirror
/// <see cref="StoreWrapper.AppendNQuadsTerm"/>; the difference is that this
/// version takes plain strings (caller does the IRI-vs-literal decision).
/// </summary>
internal static class StatementSerializer
{
    public static string Dump(StatementTriple t)
    {
        var sb = new StringBuilder();
        Append(sb, t.Subject, isLiteral: false);
        sb.Append(' ');
        Append(sb, t.Predicate, isLiteral: false);
        sb.Append(' ');
        Append(sb, t.Object, isLiteral: true);
        sb.Append(" .");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string term, bool isLiteral)
    {
        if (!isLiteral)
        {
            sb.Append('<').Append(term).Append('>');
            return;
        }
        // Literal: assume the caller passed an already-quoted literal
        // (including language tag / datatype when present). We pass through
        // verbatim — production code constructs the literal string from an
        // Oxigraph Literal and the round-trip is verified by the
        // StoreWrapper tests.
        sb.Append(term);
    }
}