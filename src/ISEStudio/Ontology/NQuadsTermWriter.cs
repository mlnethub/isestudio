using System.Text;

namespace ISEStudio.Ontology;

/// <summary>
/// Single source of truth for the canonical N-Quads term-encoding rules used
/// by every code path that serializes RDF terms to bytes: store dumps,
/// conflict signatures, and layer exports. Centralising the writer removes
/// the previous byte-identical private copies (in
/// <c>ConflictDetector</c> and <c>RdfExportService</c>), which were easy to
/// drift out of sync and produced subtly different signatures if any one
/// site was edited.
/// </summary>
/// <remarks>
/// <para>Output rules per term kind:</para>
/// <list type="bullet">
///   <item><description><see cref="RdfIri"/>: <c>&lt;iri&gt;</c></description></item>
///   <item><description><see cref="RdfBlankNode"/>: <c>_:label</c></description></item>
///   <item><description><see cref="RdfLiteral"/>: <c>"escaped"</c>, with
///     <c>@lang</c> when the literal carries a language tag, otherwise
///     <c>^^&lt;datatype&gt;</c> (defaulting to <c>xsd:string</c> when no
///     datatype was attached).</description></item>
/// </list>
/// </remarks>
internal static class NQuadsTermWriter
{
    /// <summary>
    /// Append the canonical N-Quads encoding of an <see cref="RdfTerm"/>
    /// to <paramref name="sb"/>. This is the runtime RDF boundary's term
    /// writer.
    /// </summary>
    public static void Append(StringBuilder sb, RdfTerm term)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(term);
        switch (term)
        {
            case RdfIri iri:
                sb.Append('<').Append(iri.Value).Append('>');
                break;
            case RdfBlankNode blank:
                sb.Append("_:").Append(blank.Id);
                break;
            case RdfLiteral literal:
                AppendLiteral(sb, literal);
                break;
            default:
                throw new InvalidOperationException($"Unsupported RDF term: {term.GetType().Name}");
        }
    }

    private static void AppendLiteral(StringBuilder sb, RdfLiteral literal)
    {
        sb.Append('"');
        foreach (var ch in literal.Value)
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
        if (!string.IsNullOrEmpty(literal.Language))
        {
            sb.Append('@').Append(literal.Language);
        }
        else
        {
            // N-Triples spec: a literal without a datatype defaults to xsd:string.
            var dt = literal.Datatype ?? "http://www.w3.org/2001/XMLSchema#string";
            sb.Append("^^<").Append(dt).Append('>');
        }
    }
}