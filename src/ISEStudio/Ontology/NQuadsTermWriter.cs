using System.Text;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Ontology;

/// <summary>
/// Single source of truth for the canonical N-Quads term-encoding rules used
/// by every code path that serializes RDF terms to bytes: store dumps,
/// conflict signatures, and layer exports. Centralising the writer removes
/// the previous triple of byte-identical private copies (in
/// <c>StoreWrapper</c>, <c>ConflictDetector</c>, and <c>RdfExportService</c>),
/// which were easy to drift out of sync and produced subtly different
/// signatures if any one site was edited.
/// </summary>
/// <remarks>
/// <para>Output rules per term kind:</para>
/// <list type="bullet">
///   <item><description><see cref="OntoNamedNode"/>: <c>&lt;iri&gt;</c></description></item>
///   <item><description><see cref="OntoBlankNode"/>: <c>_:label</c></description></item>
///   <item><description><see cref="OntoLiteral"/>: <c>"escaped"</c>, with
///     <c>@lang</c> when the literal carries a language tag, otherwise
///     <c>^^&lt;datatype&gt;</c> (defaulting to <c>xsd:string</c> when no
///     datatype was attached).</description></item>
///   <item><description>Anything else (e.g. <c>DefaultGraph</c>): fall back
///     to <see cref="object.ToString"/>.</description></item>
/// </list>
/// <para>Byte-for-byte identical to the previous in-place implementations;
/// the centralised form is verified by
/// <c>NQuadsTermWriterTests.three_call_sites_produce_identical_bytes_for</c>
/// so any future change here that drifts from the old behaviour fails
/// loudly.</para>
/// </remarks>
internal static class NQuadsTermWriter
{
    /// <summary>
    /// Append the canonical N-Quads encoding of <paramref name="term"/> to
    /// <paramref name="sb"/>. Does not emit any leading/trailing whitespace.
    /// </summary>
    public static void Append(StringBuilder sb, object term)
    {
        ArgumentNullException.ThrowIfNull(sb);
        switch (term)
        {
            case OntoNamedNode n:
                sb.Append('<').Append(n.Value).Append('>');
                break;
            case OntoBlankNode b:
                sb.Append("_:").Append(b.Value);
                break;
            case OntoLiteral l:
                // N-Quads: escape backslashes and double quotes; add language
                // tag if present; add datatype IRI otherwise. Strings without
                // a datatype default to xsd:string (per W3C N-Triples spec).
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
                // DefaultGraph and any future term types: fall back to ToString().
                sb.Append(term.ToString());
                break;
        }
    }
}
