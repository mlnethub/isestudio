using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoTriple = Oxigraph.Triple;

namespace ISEStudio.Ontology;

public sealed class RdfImportException : Exception
{
    public RdfImportException(string message) : base(message) { }
}

public sealed record ParsedRdfImport(string Format, IReadOnlyList<OntoTriple> Triples);

public sealed record RdfImportPartition(IReadOnlyList<OntoTriple> TBox, IReadOnlyList<OntoTriple> ABox);

/// <summary>
/// Format-aware RDF parser + TBox/ABox partitioner used by
/// <see cref="RdfImportService"/>. Accepts the same format aliases as the
/// Python <c>backend/app/api/rdf_import.py</c>: <c>auto</c>, <c>turtle</c>,
/// <c>rdfxml</c>, <c>ntriples</c>, and <c>jsonld</c>. Blank nodes are
/// scoped per-import so two imports against the same graph never collide
/// on a reused label. The parsed triple list is enforced against
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

    public ParsedRdfImport Parse(byte[] data, string filename, string requestedFormat, string? baseIri, int? maxTriples, string blankNodeScope)
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
                var triples = ParseWithDotNetRdf(data, format, baseIri, maxTriples, blankNodeScope);
                return new ParsedRdfImport(format, triples);
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

    public RdfImportPartition Partition(IReadOnlyList<OntoTriple> triples, string target)
    {
        var normalized = target.Trim().ToLowerInvariant();
        if (normalized == "tbox") return new RdfImportPartition(triples, Array.Empty<OntoTriple>());
        if (normalized == "abox") return new RdfImportPartition(Array.Empty<OntoTriple>(), triples);
        if (normalized != "auto") throw new RdfImportException($"Unsupported RDF import target: {target}");
        return SplitTBoxABox(triples);
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

    private static IReadOnlyList<OntoTriple> ParseWithDotNetRdf(byte[] data, string format, string? baseIri, int? maxTriples, string blankNodeScope)
    {
        var graph = new Graph();
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

        var blankNodes = new Dictionary<string, OntoBlankNode>(StringComparer.Ordinal);
        var triples = new List<OntoTriple>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var triple in graph.Triples)
        {
            if (maxTriples is not null && triples.Count + 1 > maxTriples.Value)
            {
                throw new RdfImportException($"RDF file exceeds the {maxTriples.Value:N0}-triple import limit");
            }
            var converted = new OntoTriple(
                ToSubject(triple.Subject, blankNodes, blankNodeScope),
                ToPredicate(triple.Predicate),
                ToObject(triple.Object, blankNodes, blankNodeScope));
            var key = $"{converted.Subject}|{converted.Predicate}|{converted.Object}";
            if (seen.Add(key)) triples.Add(converted);
        }
        return triples;
    }

    private static Oxigraph.INamedOrBlankNode ToSubject(INode node, Dictionary<string, OntoBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new OntoNamedNode(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => GetOrAddBlank(blanks, ((IBlankNode)node).InternalID, scope),
        _ => throw new RdfImportException($"Unsupported RDF subject node: {node.NodeType}"),
    };

    private static OntoNamedNode ToPredicate(INode node)
    {
        if (node is IUriNode uri) return new OntoNamedNode(uri.Uri.AbsoluteUri);
        throw new RdfImportException($"Unsupported RDF predicate node: {node.NodeType}");
    }

    private static Oxigraph.ITerm ToObject(INode node, Dictionary<string, OntoBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new OntoNamedNode(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => GetOrAddBlank(blanks, ((IBlankNode)node).InternalID, scope),
        NodeType.Literal => ToLiteral((ILiteralNode)node),
        _ => throw new RdfImportException($"Unsupported RDF object node: {node.NodeType}"),
    };

    private static OntoBlankNode GetOrAddBlank(Dictionary<string, OntoBlankNode> blanks, string internalId, string scope)
    {
        if (blanks.TryGetValue(internalId, out var existing)) return existing;
        var node = new OntoBlankNode($"rdfimport_{scope}_{blanks.Count}");
        blanks[internalId] = node;
        return node;
    }

    private static OntoLiteral ToLiteral(ILiteralNode literal)
    {
        if (!string.IsNullOrEmpty(literal.Language)) return new OntoLiteral(literal.Value, Language: literal.Language);
        if (literal.DataType is not null) return new OntoLiteral(literal.Value, Datatype: new OntoNamedNode(literal.DataType.AbsoluteUri));
        return new OntoLiteral(literal.Value);
    }

    private static RdfImportPartition SplitTBoxABox(IReadOnlyList<OntoTriple> triples)
    {
        var schemaNodes = new HashSet<object>();
        foreach (var triple in triples)
        {
            var predicate = triple.Predicate.Value;
            var objectIri = triple.Object is OntoNamedNode node ? node.Value : null;
            if (predicate == Vocabulary.RdfType.Value && objectIri is not null && SchemaTypes.Contains(objectIri))
            {
                schemaNodes.Add(triple.Subject);
            }
            if (SchemaSubjectPredicates.Contains(predicate))
            {
                schemaNodes.Add(triple.Subject);
            }
            if ((ClassLinkPredicates.Contains(predicate) || PropertyLinkPredicates.Contains(predicate))
                && triple.Object is Oxigraph.INamedOrBlankNode linked)
            {
                schemaNodes.Add(linked);
            }
        }
        var tbox = new List<OntoTriple>();
        var abox = new List<OntoTriple>();
        foreach (var triple in triples)
        {
            (schemaNodes.Contains(triple.Subject) ? tbox : abox).Add(triple);
        }
        return new RdfImportPartition(tbox, abox);
    }

    private static string Owl(string local) => Vocabulary.Owl + local;

    private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfType.Value,
        Vocabulary.RdfsClass.Value,
        Vocabulary.RdfsDatatype.Value,
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
        Vocabulary.RdfsSubClassOf.Value, Vocabulary.RdfsDomain.Value, Vocabulary.RdfsRange.Value,
        Owl("equivalentClass"), Owl("disjointWith"), Owl("complementOf"),
        Owl("onClass"), Owl("onDataRange"), Owl("someValuesFrom"), Owl("allValuesFrom"),
        "http://www.w3.org/ns/shacl#class", "http://www.w3.org/ns/shacl#targetClass",
        "http://www.w3.org/ns/shacl#datatype",
    };

    private static readonly HashSet<string> PropertyLinkPredicates = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfsSubPropertyOf.Value, Owl("equivalentProperty"),
        Owl("propertyDisjointWith"), Owl("inverseOf"), Owl("onProperty"),
        "http://www.w3.org/ns/shacl#path",
    };

    private static readonly HashSet<string> SchemaSubjectPredicates = new(
        ClassLinkPredicates.Concat(PropertyLinkPredicates), StringComparer.Ordinal);
}