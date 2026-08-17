using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Ontology;

/// <summary>
/// RDO / SKOS / OWL / RDF / RDFS vocabulary constants used by the TBox layer.
/// All IRIs match the canonical W3C namespaces; the helper
/// <see cref="ClassLocalName"/> / <see cref="PropertyLocalName"/> / <see cref="NormLabel"/>
/// trio is the .NET equivalent of the Python <c>vocab.py</c> label helpers and is
/// shared with <see cref="SchemaBuilder"/> and <see cref="Guard"/>.
/// </summary>
public static class Vocabulary
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public const string Owl = "http://www.w3.org/2002/07/owl#";
    public const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    public static readonly OntoNamedNode RdfType = new(Rdf + "type");
    public static readonly OntoNamedNode RdfFirst = new(Rdf + "first");
    public static readonly OntoNamedNode RdfRest = new(Rdf + "rest");
    public static readonly OntoNamedNode RdfNil = new(Rdf + "nil");

    public static readonly OntoNamedNode RdfsLabel = new(Rdfs + "label");
    public static readonly OntoNamedNode RdfsComment = new(Rdfs + "comment");
    public static readonly OntoNamedNode RdfsSubClassOf = new(Rdfs + "subClassOf");
    public static readonly OntoNamedNode RdfsSubPropertyOf = new(Rdfs + "subPropertyOf");
    public static readonly OntoNamedNode RdfsDomain = new(Rdfs + "domain");
    public static readonly OntoNamedNode RdfsRange = new(Rdfs + "range");

    public static readonly OntoNamedNode OwlClass = new(Owl + "Class");
    public static readonly OntoNamedNode OwlNamedIndividual = new(Owl + "NamedIndividual");
    public static readonly OntoNamedNode OwlObjectProperty = new(Owl + "ObjectProperty");
    public static readonly OntoNamedNode OwlDatatypeProperty = new(Owl + "DatatypeProperty");
    public static readonly OntoNamedNode OwlDisjointWith = new(Owl + "disjointWith");
    public static readonly OntoNamedNode OwlEquivalentClass = new(Owl + "equivalentClass");
    public static readonly OntoNamedNode OwlUnionOf = new(Owl + "unionOf");

    public static readonly OntoNamedNode XsdString = new(Xsd + "string");

    // Canonical XSD alias table (mirrors _XSD_MAP in schema.py).
    private static readonly Dictionary<string, string> XsdAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = "string",
        ["text"] = "string",
        ["str"] = "string",
        ["integer"] = "integer",
        ["int"] = "integer",
        ["long"] = "integer",
        ["float"] = "decimal",
        ["double"] = "decimal",
        ["decimal"] = "decimal",
        ["number"] = "decimal",
        ["boolean"] = "boolean",
        ["bool"] = "boolean",
        ["date"] = "date",
        ["datetime"] = "dateTime",
        ["time"] = "time",
        ["uri"] = "anyURI",
        ["url"] = "anyURI",
        ["anyuri"] = "anyURI",
    };

    /// <summary>
    /// Resolve a model-emitted datatype token (e.g. "int", "xsd:integer",
    /// "http://www.w3.org/2001/XMLSchema#integer") to its canonical XSD local
    /// name ("integer"). Returns <c>null</c> when the token is not an XSD alias.
    /// </summary>
    public static string? CanonicalDatatypeName(string? value)
    {
        if (value is null) return null;
        var token = value.Normalize(System.Text.NormalizationForm.FormKC).Trim().Trim('<', '>').ToLowerInvariant();
        if (token.StartsWith(Xsd.ToLowerInvariant(), StringComparison.Ordinal))
        {
            token = token[Xsd.Length..];
        }
        else if (token.StartsWith("xsd:", StringComparison.Ordinal))
        {
            token = token[4..];
        }
        else if (token.Contains("xmlschema#", StringComparison.OrdinalIgnoreCase))
        {
            token = token.Split("xmlschema#", 2, StringSplitOptions.TrimEntries)[^1];
        }
        return XsdAliases.TryGetValue(token, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// Resolve a datatype token to the canonical <c>xsd:</c> IRI node. Falls
    /// back to <c>xsd:string</c> when no alias is recognised.
    /// </summary>
    public static OntoNamedNode DatatypeNode(string? name)
    {
        var canonical = CanonicalDatatypeName(name) ?? "string";
        return new OntoNamedNode(Xsd + canonical);
    }

    // ------------------------------------------------------------------
    // Label / IRI helpers (port of vocab.py: _words, norm_label,
    // class_local_name, property_local_name).
    // ------------------------------------------------------------------

    // Split on ASCII punctuation/space but keep non-ASCII letters so CJK /
    // accented labels produce stable local names.
    private static readonly System.Text.RegularExpressions.Regex WordSplit =
        new(@"[^0-9A-Za-z-￿]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CamelBoundary =
        new(@"(?<=[a-z0-9])(?=[A-Z])", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<string> Words(string label)
    {
        var pieces = new List<string>();
        foreach (var token in WordSplit.Split(label.Trim()))
        {
            if (string.IsNullOrEmpty(token)) continue;
            foreach (var w in CamelBoundary.Split(token))
            {
                if (!string.IsNullOrEmpty(w)) pieces.Add(w);
            }
        }
        return pieces;
    }

    /// <summary>Case/separator-insensitive key for exact-duplicate detection.</summary>
    public static string NormLabel(string label) =>
        string.Join(' ', Words(label).Select(w => w.ToLowerInvariant()));

    /// <summary>PascalCase local name for a class IRI; also acts as dedup key.</summary>
    public static string ClassLocalName(string label)
    {
        var words = Words(label);
        if (words.Count == 0) return "Class";
        return string.Concat(words.Select(w =>
            string.IsNullOrEmpty(w) ? "" : char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..].ToLowerInvariant() : "")));
    }

    /// <summary>camelCase local name for a property IRI; also acts as dedup key.</summary>
    public static string PropertyLocalName(string label)
    {
        var words = Words(label);
        if (words.Count == 0) return "property";
        var head = words[0].ToLowerInvariant();
        var tail = string.Concat(words.Skip(1).Select(w =>
            string.IsNullOrEmpty(w) ? "" : char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..].ToLowerInvariant() : "")));
        return head + tail;
    }

    /// <summary>Convenience: build a class IRI by local name from a label.</summary>
    public static OntoNamedNode ClassNode(string baseIri, string label) =>
        new(baseIri + ClassLocalName(label));

    /// <summary>Convenience: build a property IRI by local name from a label.</summary>
    public static OntoNamedNode PropertyNode(string baseIri, string label) =>
        new(baseIri + PropertyLocalName(label));
}