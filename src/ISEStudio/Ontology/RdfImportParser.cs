using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace ISEStudio.Ontology;

public sealed class RdfImportException : Exception
{
    public RdfImportException(string message) : base(message) { }
}

public sealed record ParsedRdfDocument(string Format, IReadOnlyList<RdfStatement> Statements);

public sealed record RdfImportPartition(IReadOnlyList<RdfStatement> TBox, IReadOnlyList<RdfStatement> ABox);

/// <summary>
/// Format-aware RDF parser + TBox/ABox partitioner used by
/// <see cref="RdfImportService"/>. Accepts the same format aliases as the
/// Python <c>backend/app/api/rdf_import.py</c>: <c>auto</c>, <c>turtle</c>,
/// <c>rdfxml</c>, <c>ntriples</c>, and <c>jsonld</c>. Blank nodes are
/// scoped per-import so two imports against the same graph never collide
/// on a reused label. The parsed statement list is enforced against
/// <c>ISEStudio:RdfImportMaxTriples</c> at parse time, not at write time.
/// </summary>
public sealed class RdfImportParser
{
    private static readonly IReadOnlyDictionary<string, string> FormatAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ttl"] = "turtle",
        ["turtle"] = "turtle",
        ["rdf"] = "rdfxml",
        ["rdf/xml"] = "rdfxml",
        ["rdfxml"] = "rdfxml",
        ["xml"] = "rdfxml",
        ["nt"] = "ntriples",
        ["n-triples"] = "ntriples",
        ["ntriples"] = "ntriples",
        ["json"] = "jsonld",
        ["json-ld"] = "jsonld",
        ["jsonld"] = "jsonld",
    };

    private static readonly IReadOnlyDictionary<string, string> ExtensionFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".ttl"] = "turtle",
        [".rdf"] = "rdfxml",
        [".xml"] = "rdfxml",
        [".nt"] = "ntriples",
        [".jsonld"] = "jsonld",
        [".json"] = "jsonld",
    };

    public ParsedRdfDocument Parse(byte[] data, string filename, string requestedFormat, string? baseIri, int? maxTriples, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(data)))
        {
            throw new RdfImportException("The RDF file is empty");
        }

        var errors = new List<string>();
        foreach (var format in CandidateFormats(data, filename, requestedFormat))
        {
            try
            {
                var statements = ParseWithDotNetRdf(data, format, baseIri, maxTriples, blankNodeScope);
                return new ParsedRdfDocument(format, statements);
            }
            catch (RdfImportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{format}: {ex.Message}");
                if (!string.Equals(NormalizeFormat(requestedFormat), "auto", StringComparison.Ordinal)) break;
            }
        }
        throw new RdfImportException($"Could not parse RDF ({(errors.Count == 0 ? "unknown parser error" : errors[0])})");
    }

    public RdfImportPartition Partition(IReadOnlyList<RdfStatement> statements, string target)
    {
        var normalized = target.Trim().ToLowerInvariant();
        if (normalized == "tbox") return new RdfImportPartition(statements, Array.Empty<RdfStatement>());
        if (normalized == "abox") return new RdfImportPartition(Array.Empty<RdfStatement>(), statements);
        if (normalized != "auto") throw new RdfImportException($"Unsupported RDF import target: {target}");
        return SplitTBoxABox(statements);
    }

    public static string NormalizeFormat(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "auto") return normalized;
        if (FormatAliases.TryGetValue(normalized, out var canonical)) return canonical;
        throw new RdfImportException($"Unsupported RDF format: {value}");
    }

    private static IReadOnlyList<string> CandidateFormats(byte[] data, string filename, string requested)
    {
        var normalized = NormalizeFormat(requested);
        if (normalized != "auto") return [normalized];
        var ext = Path.GetExtension(filename ?? string.Empty);
        var first = ExtensionFormats.TryGetValue(ext, out var byExt) ? byExt : SniffFormat(data);
        return new[] { first }.Concat(FormatAliases.Values.Distinct(StringComparer.Ordinal).Where(f => f != first)).ToList();
    }

    private static string SniffFormat(byte[] data)
    {
        var head = Encoding.UTF8.GetString(data).TrimStart().ToLowerInvariant();
        if (head.StartsWith("{") || head.StartsWith("[")) return "jsonld";
        if (head.StartsWith("<?xml", StringComparison.Ordinal) || head.Contains("<rdf:rdf", StringComparison.Ordinal)) return "rdfxml";
        if (head.StartsWith("@prefix", StringComparison.Ordinal) || head.StartsWith("prefix ", StringComparison.Ordinal) || head.Contains("@prefix ", StringComparison.Ordinal)) return "turtle";
        return head.StartsWith("<", StringComparison.Ordinal) ? "ntriples" : "turtle";
    }

    private static IReadOnlyList<RdfStatement> ParseWithDotNetRdf(byte[] data, string format, string? baseIri, int? maxTriples, string blankNodeScope)
    {
        var graph = new VDS.RDF.Graph();
        if (!string.IsNullOrWhiteSpace(baseIri))
        {
            try { graph.BaseUri = new Uri(baseIri, UriKind.RelativeOrAbsolute); }
            catch { /* graph.BaseUri tolerates most inputs; ignore malformed bases */ }
        }
        var text = Encoding.UTF8.GetString(data);
        IRdfReader parser = format switch
        {
            "turtle" => new TurtleParser(),
            "rdfxml" => new RdfXmlParser(),
            "ntriples" => new NTriplesParser(),
            "jsonld" => throw new RdfImportException("Could not parse RDF (jsonld: JSON-LD parser is unavailable)"),
            _ => throw new RdfImportException($"Unsupported RDF format: {format}"),
        };
        parser.Load(graph, new StringReader(text));

        var blankNodes = new Dictionary<string, RdfBlankNode>(StringComparer.Ordinal);
        var statements = new List<RdfStatement>();
        var seen = new HashSet<RdfStatement>();
        foreach (var triple in graph.Triples)
        {
            if (maxTriples is not null && statements.Count + 1 > maxTriples.Value)
            {
                throw new RdfImportException($"RDF file exceeds the {maxTriples.Value:N0}-triple import limit");
            }
            var converted = new RdfStatement(
                ToSubject(triple.Subject, blankNodes, blankNodeScope),
                ToPredicate(triple.Predicate),
                ToObject(triple.Object, blankNodes, blankNodeScope));
            if (seen.Add(converted)) statements.Add(converted);
        }
        return statements;
    }

    private static RdfTerm ToSubject(INode node, Dictionary<string, RdfBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new RdfIri(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => GetOrAddBlank(blanks, ((IBlankNode)node).InternalID, scope),
        _ => throw new RdfImportException($"Unsupported RDF subject node: {node.NodeType}"),
    };

    private static string ToPredicate(INode node)
    {
        if (node is IUriNode uri) return uri.Uri.AbsoluteUri;
        throw new RdfImportException($"Unsupported RDF predicate node: {node.NodeType}");
    }

    private static RdfTerm ToObject(INode node, Dictionary<string, RdfBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new RdfIri(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => GetOrAddBlank(blanks, ((IBlankNode)node).InternalID, scope),
        NodeType.Literal => ToLiteral((ILiteralNode)node),
        _ => throw new RdfImportException($"Unsupported RDF object node: {node.NodeType}"),
    };

    private static RdfBlankNode GetOrAddBlank(Dictionary<string, RdfBlankNode> blanks, string internalId, string scope)
    {
        if (blanks.TryGetValue(internalId, out var existing)) return existing;
        var node = new RdfBlankNode($"rdfimport_{scope}_{blanks.Count}");
        blanks[internalId] = node;
        return node;
    }

    private static RdfLiteral ToLiteral(ILiteralNode literal)
    {
        if (!string.IsNullOrEmpty(literal.Language)) return new RdfLiteral(literal.Value, Language: literal.Language);
        if (literal.DataType is not null) return new RdfLiteral(literal.Value, Datatype: literal.DataType.AbsoluteUri);
        return new RdfLiteral(literal.Value);
    }

    private static RdfImportPartition SplitTBoxABox(IReadOnlyList<RdfStatement> statements)
    {
        var schemaNodes = new HashSet<RdfTerm>();
        foreach (var statement in statements)
        {
            var predicate = statement.PredicateIri;
            var objectIri = statement.Object is RdfIri iri ? iri.Value : null;
            if (predicate == Vocabulary.RdfType && objectIri is not null && SchemaTypes.Contains(objectIri))
            {
                schemaNodes.Add(statement.Subject);
            }
            if (SchemaSubjectPredicates.Contains(predicate))
            {
                schemaNodes.Add(statement.Subject);
            }
            if ((ClassLinkPredicates.Contains(predicate) || PropertyLinkPredicates.Contains(predicate))
                && statement.Object is RdfIri or RdfBlankNode)
            {
                schemaNodes.Add(statement.Object);
            }
        }
        var tbox = new List<RdfStatement>();
        var abox = new List<RdfStatement>();
        foreach (var statement in statements)
        {
            (schemaNodes.Contains(statement.Subject) ? tbox : abox).Add(statement);
        }
        return new RdfImportPartition(tbox, abox);
    }

    private static string Owl(string local) => Vocabulary.Owl + local;

    private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfType,
        Vocabulary.RdfsClass,
        Vocabulary.RdfsDatatype,
        Owl("Class"), Owl("Restriction"), Owl("Ontology"), Owl("ObjectProperty"),
        Owl("DatatypeProperty"), Owl("AnnotationProperty"), Owl("OntologyProperty"),
        Owl("FunctionalProperty"), Owl("InverseFunctionalProperty"), Owl("TransitiveProperty"),
        Owl("SymmetricProperty"), Owl("AsymmetricProperty"), Owl("ReflexiveProperty"),
        Owl("IrreflexiveProperty"), Owl("DeprecatedClass"), Owl("DeprecatedProperty"),
        Owl("AllDisjointClasses"), Owl("AllDisjointProperties"),
        "http://www.w3.org/ns/shacl#NodeShape", "http://www.w3.org/ns/shacl#PropertyShape",
    };

    private static readonly HashSet<string> ClassLinkPredicates = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfsSubClassOf, Vocabulary.RdfsDomain, Vocabulary.RdfsRange,
        Owl("equivalentClass"), Owl("disjointWith"), Owl("complementOf"),
        Owl("onClass"), Owl("onDataRange"), Owl("someValuesFrom"), Owl("allValuesFrom"),
        "http://www.w3.org/ns/shacl#class", "http://www.w3.org/ns/shacl#targetClass",
        "http://www.w3.org/ns/shacl#datatype",
    };

    private static readonly HashSet<string> PropertyLinkPredicates = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfsSubPropertyOf, Owl("equivalentProperty"),
        Owl("propertyDisjointWith"), Owl("inverseOf"), Owl("onProperty"),
        "http://www.w3.org/ns/shacl#path",
    };

    private static readonly HashSet<string> SchemaSubjectPredicates = new(
        ClassLinkPredicates.Concat(PropertyLinkPredicates), StringComparer.Ordinal);
}
