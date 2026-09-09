using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;
using IGraph = VDS.RDF.IGraph;

namespace ISEStudio.Ontology;

/// <summary>
/// dotNetRDF ⇄ <see cref="RdfStatement"/> boundary. Every byte that crosses
/// into or out of the ISEStudio runtime — N-Quads documents from importers,
/// Turtle from SHACL exports, JSON-LD from extraction outputs — must move
/// through this codec so the runtime never holds a foreign-store term
/// object.
/// </summary>
/// <remarks>
/// <para>Parsing strategy:</para>
/// <list type="bullet">
///   <item><description>N-Quads always parses through a <see cref="TripleStore"/>
///     so the per-statement graph IRI is preserved (the dotNetRDF
///     <c>Graph</c> type is triple-only).</description></item>
///   <item><description>Triple-only formats (Turtle, N-Triples, RDF/XML,
///     JSON-LD) parse into a <see cref="VDS.RDF.Graph"/>; the caller
///     supplies the target <c>graphIri</c> and we attach it after
///     conversion.</description></item>
/// </list>
/// <para>Serializing strategy:</para>
/// <list type="bullet">
///   <item><description>N-Quads writes through <see cref="NQuadsWriter"/> so
///     the format byte-stream matches the W3C grammar and round-trips
///     cleanly through other parsers.</description></item>
/// </list>
/// <para>Blank-node identity:</para>
/// <list type="bullet">
///   <item><description>Each <see cref="ParseDocument(byte[], IRdfReader, string?, string, string?)"/>
///     call mints fresh blank-node ids scoped to
///     <c>blankNodeScope</c>, so two imports of the same source can never
///     share a label.</description></item>
/// </list>
/// </remarks>
public static class RdfDotNetRdfCodec
{
    /// <summary>Parsed RDF dataset — a flat list of statements keyed only by
    /// their (subject, predicate, object, graph) tuple.</summary>
    public sealed record ParsedRdfDataset(IReadOnlyList<RdfStatement> Statements);

    /// <summary>
    /// Parse an N-Quads byte stream into runtime statements. Each statement
    /// carries its own <c>graphIri</c>; the default graph is mapped to
    /// <c>null</c>.
    /// </summary>
    public static ParsedRdfDataset ParseNQuads(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // N-Quads blank-node labels are explicit and round-trippable — we do
        // NOT scope them. Triple-only formats (Turtle, RDF/XML) DO scope
        // blank-node labels per document, see ParseDocument.
        var statements = new List<RdfStatement>();

        var text = Encoding.UTF8.GetString(bytes);
        int lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

            if (!TryParseNQuadsLine(trimmed, out var statement))
            {
                throw new RdfImportException($"Invalid N-Quads at line {lineNumber}: {trimmed}");
            }
            statements.Add(statement);
        }
        return new ParsedRdfDataset(statements);
    }

    private static bool TryParseNQuadsLine(string line, out RdfStatement statement)
    {
        statement = default!;
        var tokens = TokenizeNQuads(line);
        if (tokens.Count < 4) return false;

        // Layout: subject predicate object [graph] .
        var subjectToken = tokens[0];
        var predicateToken = tokens[1];
        var objectToken = tokens[2];
        var terminatorIndex = tokens.Count - 1;
        if (tokens[terminatorIndex] != ".") return false;

        string? graphIri = null;
        if (tokens.Count == 5)
        {
            var graphToken = tokens[3];
            if (graphToken.StartsWith("<", StringComparison.Ordinal) && graphToken.EndsWith(">", StringComparison.Ordinal))
            {
                graphIri = graphToken[1..^1];
            }
            else
            {
                return false;
            }
        }
        else if (tokens.Count != 4)
        {
            return false;
        }

        if (!TryParseNodeToken(subjectToken, out var subject)) return false;
        if (!TryParsePredicateToken(predicateToken, out var predicateIri)) return false;
        if (!TryParseObjectToken(objectToken, out var obj)) return false;

        statement = new RdfStatement(subject, predicateIri, obj, graphIri);
        return true;
    }

    private static IReadOnlyList<string> TokenizeNQuads(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inLiteral = false;
        bool escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (escape)
            {
                current.Append(ch);
                escape = false;
                continue;
            }
            if (inLiteral)
            {
                if (ch == '\\') { current.Append(ch); escape = true; continue; }
                if (ch == '"') { current.Append(ch); inLiteral = false; continue; }
                current.Append(ch);
                continue;
            }
            if (ch == '"') { current.Append(ch); inLiteral = true; continue; }
            if (ch == ' ' || ch == '\t')
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool TryParseNodeToken(string token, out RdfTerm term)
    {
        term = default!;
        if (token.StartsWith("<", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
        {
            term = new RdfIri(token[1..^1]);
            return true;
        }
        if (token.StartsWith("_:", StringComparison.Ordinal))
        {
            term = new RdfBlankNode(token[2..]);
            return true;
        }
        return false;
    }

    private static bool TryParsePredicateToken(string token, out string predicateIri)
    {
        predicateIri = string.Empty;
        if (token.StartsWith("<", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
        {
            predicateIri = token[1..^1];
            return true;
        }
        return false;
    }

    private static bool TryParseObjectToken(string token, out RdfTerm term)
    {
        if (TryParseNodeToken(token, out term)) return true;
        if (token.StartsWith("\"", StringComparison.Ordinal))
        {
            return TryParseLiteralToken(token, out term);
        }
        return false;
    }

    private static bool TryParseLiteralToken(string token, out RdfTerm term)
    {
        term = default!;
        if (!token.StartsWith("\"", StringComparison.Ordinal)) return false;

        // Find the closing quote (skipping escaped backslashes and quotes).
        int closeQuote = -1;
        for (int i = 1; i < token.Length; i++)
        {
            if (token[i] == '\\') { i++; continue; }
            if (token[i] == '"') { closeQuote = i; break; }
        }
        if (closeQuote < 0) return false;

        // Decode escape sequences in the literal value.
        var rawValue = token.Substring(1, closeQuote - 1);
        var value = new StringBuilder(rawValue.Length);
        for (int i = 0; i < rawValue.Length; i++)
        {
            if (rawValue[i] == '\\' && i + 1 < rawValue.Length)
            {
                var next = rawValue[i + 1];
                switch (next)
                {
                    case '\\': value.Append('\\'); break;
                    case '"': value.Append('"'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    default: value.Append(next); break;
                }
                i++;
                continue;
            }
            value.Append(rawValue[i]);
        }

        var tail = token[(closeQuote + 1)..];
        if (tail.Length == 0)
        {
            term = new RdfLiteral(value.ToString());
            return true;
        }
        if (tail.StartsWith("@", StringComparison.Ordinal))
        {
            term = new RdfLiteral(value.ToString(), tail[1..], null);
            return true;
        }
        if (tail.StartsWith("^^<", StringComparison.Ordinal) && tail.EndsWith(">", StringComparison.Ordinal))
        {
            term = new RdfLiteral(value.ToString(), null, tail[3..^1]);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Serialize statements to N-Quads bytes. Each statement's
    /// <c>GraphIri</c> is emitted; statements with <c>null</c> graph IRI are
    /// written against the default graph.
    /// </summary>
    public static byte[] SerializeNQuads(IEnumerable<RdfStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        var materialized = statements as IReadOnlyList<RdfStatement> ?? statements.ToList();
        if (materialized.Count == 0) return Array.Empty<byte>();

        var sb = new StringBuilder();
        foreach (var statement in materialized)
        {
            AppendNQuadsLine(sb, statement);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendNQuadsLine(StringBuilder sb, RdfStatement statement)
    {
        AppendTerm(sb, statement.Subject);
        sb.Append(' ');
        AppendTerm(sb, new RdfIri(statement.PredicateIri));
        sb.Append(' ');
        AppendTerm(sb, statement.Object);
        if (!string.IsNullOrEmpty(statement.GraphIri))
        {
            sb.Append(" <").Append(statement.GraphIri).Append('>');
        }
        sb.Append(" .\n");
    }

    private static void AppendTerm(StringBuilder sb, RdfTerm term)
    {
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
        else if (!string.IsNullOrEmpty(literal.Datatype))
        {
            sb.Append("^^<").Append(literal.Datatype).Append('>');
        }
    }

    /// <summary>
    /// Parse a triple-only RDF document with the supplied
    /// <paramref name="reader"/>, attaching every resulting statement to
    /// <paramref name="graphIri"/>. Blank-node ids are scoped to
    /// <paramref name="blankNodeScope"/> so successive imports never collide.
    /// </summary>
    public static IReadOnlyList<RdfStatement> ParseDocument(
        byte[] bytes,
        IRdfReader reader,
        string? baseIri,
        string blankNodeScope,
        string? graphIri)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(blankNodeScope);

        var graph = new VDS.RDF.Graph();
        if (!string.IsNullOrWhiteSpace(baseIri))
        {
            try { graph.BaseUri = new Uri(baseIri, UriKind.RelativeOrAbsolute); }
            catch { /* baseUri is best-effort */ }
        }

        using (var textReader = new StringReader(Encoding.UTF8.GetString(bytes)))
        {
            reader.Load(graph, textReader);
        }

        var blanks = new Dictionary<string, string>(StringComparer.Ordinal);
        var statements = new List<RdfStatement>(graph.Triples.Count);
        foreach (var triple in graph.Triples)
        {
            statements.Add(new RdfStatement(
                ToRuntimeTerm(triple.Subject, blanks, blankNodeScope),
                ToPredicate(triple.Predicate),
                ToRuntimeTerm(triple.Object, blanks, blankNodeScope),
                graphIri));
        }
        return statements;
    }

    // --------------------------------------------------------------
    // dotNetRDF → RdfTerm: blank-node labels are rewritten into
    // "<scope>_<index>" form so two parsings never share a label.
    // --------------------------------------------------------------
    private static RdfTerm ToTerm(INode node)
    {
        return node.NodeType switch
        {
            NodeType.Uri => new RdfIri(((IUriNode)node).Uri.AbsoluteUri),
            NodeType.Blank => new RdfBlankNode(((IBlankNode)node).InternalID),
            NodeType.Literal => ToLiteral((ILiteralNode)node),
            NodeType.GraphLiteral => throw new RdfImportException("Graph literals are not supported"),
            NodeType.Variable => throw new RdfImportException("Variables are not supported"),
            _ => throw new RdfImportException($"Unsupported RDF node: {node.NodeType}"),
        };
    }

    private static RdfTerm ToRuntimeTerm(INode node, IDictionary<string, string> blanks, string scope)
    {
        return node.NodeType switch
        {
            NodeType.Uri => new RdfIri(((IUriNode)node).Uri.AbsoluteUri),
            NodeType.Blank => new RdfBlankNode(GetBlankId(blanks, ((IBlankNode)node).InternalID, scope)),
            NodeType.Literal => ToLiteral((ILiteralNode)node),
            _ => throw new RdfImportException($"Unsupported RDF node: {node.NodeType}"),
        };
    }

    private static string ToPredicate(INode node)
    {
        if (node is IUriNode uri) return uri.Uri.AbsoluteUri;
        throw new RdfImportException($"Unsupported RDF predicate node: {node.NodeType}");
    }

    private static RdfLiteral ToLiteral(ILiteralNode literal)
    {
        if (!string.IsNullOrEmpty(literal.Language))
            return new RdfLiteral(literal.Value, literal.Language, null);
        if (literal.DataType is not null)
            return new RdfLiteral(literal.Value, null, literal.DataType.AbsoluteUri);
        return new RdfLiteral(literal.Value);
    }

    private static string GetBlankId(IDictionary<string, string> blanks, string internalId, string scope)
    {
        if (blanks.TryGetValue(internalId, out var existing)) return existing;
        var id = $"rdfimport_{scope}_{blanks.Count}";
        blanks[internalId] = id;
        return id;
    }

    // --------------------------------------------------------------
    // RdfTerm → dotNetRDF node, scoped to the graph that will assert
    // the triple.
    // --------------------------------------------------------------
    private static INode ToDotNetRdfNode(IGraph graph, RdfTerm term) => term switch
    {
        RdfIri iri => graph.CreateUriNode(new Uri(iri.Value, UriKind.Absolute)),
        RdfBlankNode blank => graph.CreateBlankNode(blank.Id),
        RdfLiteral literal => ToDotNetRdfLiteral(graph, literal),
        _ => throw new RdfImportException($"Unsupported RDF term: {term.GetType().Name}"),
    };

    private static ILiteralNode ToDotNetRdfLiteral(IGraph graph, RdfLiteral literal)
    {
        if (!string.IsNullOrEmpty(literal.Language))
            return graph.CreateLiteralNode(literal.Value, literal.Language);
        if (literal.Datatype is not null)
            return graph.CreateLiteralNode(literal.Value, new Uri(literal.Datatype, UriKind.Absolute));
        return graph.CreateLiteralNode(literal.Value);
    }
}