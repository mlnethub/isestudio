using System.Text;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Migration.Ontology;

/// <summary>
/// Migration-local N-Quads term writer. The runtime class in
/// <c>ISEStudio.Ontology.NQuadsTermWriter</c> has both RdfTerm and
/// OntoQuad overloads; the migration tool only consumes the Oxigraph
/// overloads because it reads legacy RocksDB/Oxigraph data and never
/// touches the runtime RdfTerm boundary. Kept here so the migration
/// tool has zero project reference on ISEStudio.
/// </summary>
internal static class NQuadsTermWriter
{
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
}
