using OnToPilot.Application.Foundation;

namespace OnToPilot.Ontology;

/// <summary>
/// Reads the curated TBox view out of an RDF store (live) or a
/// pre-serialized N-Quads shard (release). One pure algorithm
/// (<see cref="BuildCore"/>) feeds both adapters so the wire shape
/// matches Python `backend/app/ontology/schema.py::build_view`
/// identically for live and release endpoints.
/// </summary>
public sealed class OntologyViewBuilder
{
    /// <summary>Live TBox read via Oxigraph. Returns empty envelope when
    /// <paramref name="store"/> is null (contract-test path).</summary>
    public Task<OntologyResponse> BuildFromStoreAsync(
        StoreWrapper? store,
        string graphIri,
        CancellationToken cancellationToken)
    {
        if (store is null) return Task.FromResult(EmptyResponse());

        // Live algorithm lands in Task 3-5. This task only wires the
        // empty contract.
        var quads = store.Match(graphIri: graphIri);
        return Task.FromResult(BuildCore(quads));
    }

    /// <summary>Release TBox read from a pre-serialized N-Quads shard
    /// (no Oxigraph dependency). Used by published.ontology.</summary>
    public Task<OntologyResponse> BuildFromNQuadsAsync(
        byte[] tboxShard,
        CancellationToken cancellationToken)
    {
        var quads = ParseNQuads(tboxShard);
        return Task.FromResult(BuildCore(quads));
    }

    private static OntologyResponse EmptyResponse() => new(
        Classes: Array.Empty<OntologyClass>(),
        ObjectProperties: Array.Empty<OntologyProperty>(),
        DataProperties: Array.Empty<OntologyProperty>(),
        Axioms: new OntologyAxioms(
            SubclassOf: Array.Empty<SubclassAxiom>(),
            DisjointWith: Array.Empty<PairAxiom>(),
            EquivalentClass: Array.Empty<PairAxiom>()),
        Labels: new Dictionary<string, string>(),
        Stats: new OntologyStats(0, 0, 0),
        KnowledgeSystem: null);

    // BuildCore + ParseNQuads implemented in Tasks 3-5.

    private static OntologyResponse BuildCore(
        IEnumerable<Oxigraph.Quad> quads)
    {
        // Mirrors Python backend/app/ontology/schema.py::build_view (lines 241-371).
        // V1: classes + superclasses + properties. Task 5 adds disjoint /
        // equivalent-class axioms and the final Stats alignment.

        var classes = new Dictionary<string, OntologyClass>(StringComparer.Ordinal);
        var objectProps = new Dictionary<string, OntologyProperty>(StringComparer.Ordinal);
        var dataProps = new Dictionary<string, OntologyProperty>(StringComparer.Ordinal);
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var subclassOf = new List<SubclassAxiom>();
        var disjointWith = new List<PairAxiom>();
        var equivalentClass = new List<PairAxiom>();

        const string OwlClass = "http://www.w3.org/2002/07/owl#Class";
        const string OwlObjectProperty = "http://www.w3.org/2002/07/owl#ObjectProperty";
        const string OwlDatatypeProperty = "http://www.w3.org/2002/07/owl#DatatypeProperty";
        const string RdfsLabel = "http://www.w3.org/2000/01/rdf-schema#label";
        const string RdfsComment = "http://www.w3.org/2000/01/rdf-schema#comment";
        const string RdfsDomain = "http://www.w3.org/2000/01/rdf-schema#domain";
        const string RdfsRange = "http://www.w3.org/2000/01/rdf-schema#range";
        const string RdfsSubClassOf = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
        const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        foreach (var q in quads)
        {
            if (q.Subject is not Oxigraph.NamedNode s) continue;
            if (q.Predicate is not Oxigraph.NamedNode p) continue;
            var siri = s.Value;
            var piri = p.Value;

            if (piri == RdfType && q.Object is Oxigraph.NamedNode oType)
            {
                if (oType.Value == OwlObjectProperty)
                    objectProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
                else if (oType.Value == OwlDatatypeProperty)
                    dataProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
                else if (oType.Value == OwlClass)
                    classes.TryAdd(siri, new OntologyClass(siri, Label: null));
            }
            else if (piri == RdfsDomain && q.Object is Oxigraph.NamedNode d)
            {
                domains[siri] = d.Value;
            }
            else if (piri == RdfsRange && q.Object is Oxigraph.NamedNode rn)
            {
                ranges[siri] = rn.Value;
            }
            else if (piri == RdfsLabel && q.Object is Oxigraph.Literal lit)
            {
                labels[siri] = lit.Value;
            }
            else if (piri == RdfsComment && q.Object is Oxigraph.Literal lit2)
            {
                comments[siri] = lit2.Value;
            }
            else if (piri == RdfsSubClassOf && q.Object is Oxigraph.NamedNode sup)
            {
                subclassOf.Add(new SubclassAxiom(siri, sup.Value));
            }
            else if (piri == "http://www.w3.org/2002/07/owl#disjointWith" && q.Object is Oxigraph.NamedNode dj)
            {
                disjointWith.Add(new PairAxiom(siri, dj.Value));
            }
            else if (piri == "http://www.w3.org/2002/07/owl#equivalentClass" && q.Object is Oxigraph.NamedNode ec)
            {
                equivalentClass.Add(new PairAxiom(siri, ec.Value));
            }
        }

        var superBySub = subclassOf
            .GroupBy(a => a.Sub, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.Super).ToList(),
                StringComparer.Ordinal);

        OntologyProperty Prop(string iri, OntologyProperty seed) => seed with
        {
            Local = Local(iri),
            Label = labels.TryGetValue(iri, out var l) ? l : null,
            Comment = comments.TryGetValue(iri, out var c) ? c : "",
            Domain = domains.TryGetValue(iri, out var d) ? d : null,
            DomainLabel = domains.TryGetValue(iri, out var dn) && labels.TryGetValue(dn, out var dl) ? dl : null,
            Range = ranges.TryGetValue(iri, out var rng) ? rng : null,
            RangeLabel = ranges.TryGetValue(iri, out var rng2) && labels.TryGetValue(rng2, out var rl) ? rl : null,
        };

        var classList = classes.Keys
            .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri),
                StringComparer.Ordinal)
            .Select(iri =>
            {
                var c = classes[iri];
                return c with
                {
                    Local = Local(iri),
                    Label = labels.TryGetValue(iri, out var l) ? l : null,
                    Comment = comments.TryGetValue(iri, out var cm) ? cm : "",
                    Superclasses = superBySub.TryGetValue(iri, out var s) ? s : Array.Empty<string>(),
                };
            })
            .ToList();

        var objList = objectProps.Keys
            .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri),
                StringComparer.Ordinal)
            .Select(iri => Prop(iri, objectProps[iri]))
            .ToList();

        var datList = dataProps.Keys
            .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri),
                StringComparer.Ordinal)
            .Select(iri => Prop(iri, dataProps[iri]))
            .ToList();

        return new OntologyResponse(
            Classes: classList,
            ObjectProperties: objList,
            DataProperties: datList,
            Axioms: new OntologyAxioms(
                SubclassOf: subclassOf,
                DisjointWith: disjointWith,
                EquivalentClass: equivalentClass),
            Labels: labels,
            Stats: new OntologyStats(
                ClassCount: classList.Count,
                PropertyCount: objList.Count + datList.Count,
                AxiomCount: subclassOf.Count + disjointWith.Count + equivalentClass.Count),
            KnowledgeSystem: null);
    }

    private static string Local(string iri)
    {
        // Strip namespace using the last occurrence of the standard
        // separators: '#', '/', or ':' (the latter covers URN and
        // CURIE-style IRIs such as `urn:Animal`).
        var hashIdx = iri.LastIndexOf('#');
        var slashIdx = iri.LastIndexOf('/');
        var colonIdx = iri.LastIndexOf(':');
        var idx = Math.Max(hashIdx, Math.Max(slashIdx, colonIdx));
        return idx >= 0 ? iri[(idx + 1)..] : iri;
    }

    private static IEnumerable<Oxigraph.Quad> ParseNQuads(byte[] shard)
    {
        if (shard.Length == 0) yield break;
        var text = System.Text.Encoding.UTF8.GetString(shard);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var q = TryParseLine(line);
            if (q is not null) yield return q;
        }
    }

    private static Oxigraph.Quad? TryParseLine(string line)
    {
        var tokens = Tokenize(line);
        if (tokens.Count < 4) return null;
        if (tokens[^1] != ".") return null;
        var subject = ParseTerm(tokens[0]);
        var predicate = ParseTerm(tokens[1]);
        var obj = ParseTerm(tokens[2]);
        if (subject is not Oxigraph.INamedOrBlankNode sn
            || predicate is not Oxigraph.NamedNode pn
            || obj is null) return null;

        Oxigraph.IGraphName? graph = null;
        if (tokens.Count >= 5 && tokens[3] != ".")
        {
            var g = ParseTerm(tokens[3]);
            // ParseTerm only ever yields NamedNode / BlankNode / Literal, so a
            // `g is DefaultGraph` branch would be unreachable (CS0184). A null
            // `graph` already carries the "default graph" meaning below.
            if (g is Oxigraph.NamedNode gn) graph = gn;
            else return null;
        }

        // Oxigraph 0.5.8 declares `Quad.Graph` as non-nullable `IGraphName` but
        // accepts null at runtime to denote the default graph, so the null-forgiving
        // operator documents the intentional null rather than suppressing a defect.
        if (obj is Oxigraph.NamedNode on) return new Oxigraph.Quad(sn, pn, on, graph!);
        if (obj is Oxigraph.BlankNode ob) return new Oxigraph.Quad(sn, pn, ob, graph!);
        if (obj is Oxigraph.Literal ol) return new Oxigraph.Quad(sn, pn, ol, graph!);
        return null;
    }

    private static Oxigraph.ITerm? ParseTerm(string token)
    {
        if (token.StartsWith("<") && token.EndsWith(">"))
            return new Oxigraph.NamedNode(token[1..^1]);
        if (token.StartsWith("_:"))
            return new Oxigraph.BlankNode(token[2..]);
        if (token.StartsWith("\""))
        {
            var endQuote = token.IndexOf('"', 1);
            if (endQuote < 0) return null;
            var value = token[1..endQuote];
            var rest = token[(endQuote + 1)..];
            if (rest.StartsWith("@"))
                return new Oxigraph.Literal(value, Language: rest[1..]);
            if (rest.StartsWith("^^<") && rest.EndsWith(">"))
                return new Oxigraph.Literal(value, Datatype: new Oxigraph.NamedNode(rest[3..^1]));
            return new Oxigraph.Literal(value);
        }
        return null;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            if (line[i] == '<')
            {
                var end = line.IndexOf('>', i + 1);
                if (end < 0) break;
                tokens.Add(line[i..(end + 1)]);
                i = end + 1;
            }
            else if (line[i] == '_')
            {
                var j = i;
                while (j < line.Length && !char.IsWhiteSpace(line[j])) j++;
                tokens.Add(line[i..j]);
                i = j;
            }
            else if (line[i] == '"')
            {
                var j = i + 1;
                while (j < line.Length && line[j] != '"')
                {
                    if (line[j] == '\\' && j + 1 < line.Length) j += 2;
                    else j++;
                }
                if (j >= line.Length) break;
                j++;
                if (j < line.Length && line[j] == '@')
                {
                    var k = j;
                    while (k < line.Length && !char.IsWhiteSpace(line[k])) k++;
                    tokens.Add(line[i..k]);
                    i = k;
                }
                else if (j + 1 < line.Length && line[j] == '^' && line[j + 1] == '^')
                {
                    var open = line.IndexOf('<', j);
                    var close = line.IndexOf('>', open + 1);
                    if (open < 0 || close < 0) break;
                    tokens.Add(line[i..(close + 1)]);
                    i = close + 1;
                }
                else
                {
                    tokens.Add(line[i..j]);
                    i = j;
                }
            }
            else if (line[i] == '.')
            {
                tokens.Add(".");
                i++;
            }
            else
            {
                var j = i;
                while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] != '.') j++;
                tokens.Add(line[i..j]);
                i = j;
            }
        }
        return tokens;
    }
}