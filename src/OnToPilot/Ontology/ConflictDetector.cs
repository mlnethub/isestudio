using System.Security.Cryptography;
using System.Text;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;

namespace OnToPilot.Ontology;

/// <summary>
/// Produces a stable, order-independent signature for a triple set. Two
/// captures of the same logical triples (same subjects, predicates, objects,
/// graphs — regardless of insertion order) hash to the same signature so the
/// conflict queue can deduplicate re-detected issues.
///
/// <para>Algorithm: canonicalize each quad as N-Triples (preserving blank
/// nodes, language tags, and datatypes — the same byte rules used by
/// <see cref="StoreWrapper.DumpNQuads"/>), sort the resulting lines in
/// lexicographic order, join with <c>\n</c>, then SHA-256.</para>
/// </summary>
public static class ConflictDetector
{
    /// <summary>Compute the canonical SHA-256 signature for a set of quads.</summary>
    public static string Signature(IReadOnlyList<OntoQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0)
        {
            return Sha256Hex(ReadOnlySpan<byte>.Empty);
        }

        var lines = new string[quads.Count];
        for (int i = 0; i < quads.Count; i++)
        {
            lines[i] = CanonicalNQuads(quads[i]);
        }
        Array.Sort(lines, StringComparer.Ordinal);
        var joined = string.Join("\n", lines) + "\n";
        return Sha256Hex(Encoding.UTF8.GetBytes(joined));
    }

    /// <summary>
    /// Compute the signature for raw N-Quads bytes. Lines are normalized
    /// (trimmed of trailing whitespace and a single canonical terminator
    /// appended) before sorting so a serialized payload remains stable across
    /// parsers.
    /// </summary>
    public static string Signature(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        var text = Encoding.UTF8.GetString(nQuads);
        if (text.Length == 0)
        {
            return Sha256Hex(ReadOnlySpan<byte>.Empty);
        }
        var raw = text.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (var line in raw)
        {
            // Strip trailing CR/LF/whitespace but keep the leading content
            // intact so the `<subject> <predicate> <object> .` shape is
            // preserved (the leading space before `.` is part of the
            // canonical N-Quples / N-Triples form).
            var trimmed = line.TrimEnd('\r', '\n');
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;
            lines.Add(trimmed);
        }
        lines.Sort(StringComparer.Ordinal);
        var joined = string.Join("\n", lines) + "\n";
        return Sha256Hex(Encoding.UTF8.GetBytes(joined));
    }

    private static string CanonicalNQuads(OntoQuad q)
    {
        var sb = new StringBuilder();
        AppendTerm(sb, q.Subject);
        sb.Append(' ');
        AppendTerm(sb, q.Predicate);
        sb.Append(' ');
        AppendTerm(sb, q.Object);
        sb.Append(' ');
        AppendTerm(sb, q.Graph);
        sb.Append(" .");
        return sb.ToString();
    }

    // Mirror of StoreWrapper.AppendNQuadsTerm — the StoreWrapper implementation
    // is private so we duplicate the byte-exact rules here. Both writers must
    // agree on blank-node labels (`_:label`), language tags (`@en`), and
    // datatypes (`^^<iri>`) so signatures stay comparable across the codebase.
    private static void AppendTerm(StringBuilder sb, object term)
    {
        switch (term)
        {
            case OntoNamedNode n:
                sb.Append('<').Append(n.Value).Append('>');
                break;
            case OntoBlankNode b:
                sb.Append("_:").Append(b.Value);
                break;
            case OntoLiteral l:
                sb.Append('"');
                foreach (var ch in l.Value)
                {
                    switch (ch)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"': sb.Append("\\\""); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(ch); break;
                    }
                }
                sb.Append('"');
                if (l.Language is { } lang && lang.Length > 0)
                {
                    sb.Append('@').Append(lang);
                }
                else
                {
                    var dt = l.Datatype ?? OntoLiteral.XsdString;
                    sb.Append("^^<").Append(dt.Value).Append('>');
                }
                break;
            default:
                sb.Append(term.ToString());
                break;
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}