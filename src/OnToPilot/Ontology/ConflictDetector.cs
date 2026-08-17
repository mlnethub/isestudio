using System.Security.Cryptography;
using System.Text;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;

namespace OnToPilot.Ontology;

/// <summary>
/// Produces a stable, order-independent signature for a triple set. Two
/// captures of the same logical triples (same subjects, predicates, objects,
/// graphs — regardless of insertion order, and regardless of which overload
/// was used to feed the input) hash to the same signature so the conflict
/// queue can deduplicate re-detected issues.
///
/// <para>Algorithm: route every input — whether a list of <see cref="OntoQuad"/>
/// or a raw N-Quads byte payload — through the same canonical N-Quads writer
/// (<see cref="AppendTerm"/>), sort the resulting lines in lexicographic
/// order, join with <c>\n</c>, then SHA-256.</para>
///
/// <para>The byte overload parses the payload via Oxigraph's N-Quads loader
/// and re-serializes each parsed quad through the canonical writer. This
/// makes <c>Signature(bytesFromDump)</c> and <c>Signature(quadsFromMatch)</c>
/// return identical hashes for the same layer — verified by
/// <c>Signature_is_consistent_between_byte_and_quad_overloads</c>.</para>
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
    /// Compute the signature for raw N-Quads bytes. The bytes are parsed via
    /// Oxigraph's N-Quads loader and each parsed quad is re-serialized
    /// through the same canonical writer the quad overload uses, so the
    /// two overloads agree on identical logical input. Empty payloads hash
    /// to the well-known SHA-256 of the empty string.
    /// </summary>
    public static string Signature(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        var text = Encoding.UTF8.GetString(nQuads);
        if (text.Length == 0)
        {
            return Sha256Hex(ReadOnlySpan<byte>.Empty);
        }

        // Parse the bytes back to a quad set, then hash via the quad overload
        // so the byte and quad paths produce identical hashes for the same
        // logical content. Oxigraph may reassign blank-node labels on load;
        // the signature is therefore a semantic fingerprint (post-parse
        // canonical form), not a raw byte fingerprint.
        var quads = new List<OntoQuad>();
        using (var store = new Oxigraph.Store())
        {
            store.Load(text, RdfFormat.NQuads);
            foreach (var q in store.Match())
            {
                quads.Add(q);
            }
        }
        return Signature(quads);
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