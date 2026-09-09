using ISEStudio.Application.Foundation;

namespace ISEStudio.Ontology;

/// <summary>
/// Reads the curated TBox view out of PostgreSQL statements or a
/// pre-serialized N-Quads shard (release). One pure algorithm
/// (<see cref="BuildCore"/>) feeds both adapters so the wire shape
/// matches Python `backend/app/ontology/schema.py::build_view`
/// identically for live and release endpoints.
/// </summary>
/// <remarks>
/// The algorithm hot path operates directly on <see cref="RdfStatement"/>
/// — there is no foreign-store round-trip on either adapter. The byte
/// adapter parses N-Quads into <see cref="RdfStatement"/> values via
/// <see cref="RdfDotNetRdfCodec.ParseNQuads"/> (Task 1 codec) so the
/// release path matches the runtime statement path byte-for-byte at
/// the shape level. This class lives entirely on the RdfStatement
/// boundary.
/// </remarks>
public sealed class OntologyViewBuilder
{
    public Task<OntologyResponse> BuildFromStatementsAsync(
        IReadOnlyList<RdfStatement> statements,
        string graphIri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inGraph = statements
            .Where(statement => statement.GraphIri == graphIri)
            .ToList();
        return Task.FromResult(BuildCore(inGraph));
    }

    /// <summary>Release TBox read from a pre-serialized N-Quads shard.
    /// Uses the Task 1 <see cref="RdfDotNetRdfCodec"/> to parse into
    /// <see cref="RdfStatement"/> values so the algorithm path is the
    /// same as the runtime statement path.</summary>
    public Task<OntologyResponse> BuildFromNQuadsAsync(
        byte[] tboxShard,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = RdfDotNetRdfCodec.ParseNQuads(tboxShard);
        return Task.FromResult(BuildCore(parsed.Statements));
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

    private static OntologyResponse BuildCore(
        IEnumerable<RdfStatement> statements)
    {
        // Mirrors Python backend/app/ontology/schema.py::build_view (lines 241-371).
        // V1: classes + superclasses + properties. Task 5 added disjoint /
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
        const string OwlDisjointWith = "http://www.w3.org/2002/07/owl#disjointWith";
        const string OwlEquivalentClass = "http://www.w3.org/2002/07/owl#equivalentClass";
        const string RdfsLabel = "http://www.w3.org/2000/01/rdf-schema#label";
        const string RdfsComment = "http://www.w3.org/2000/01/rdf-schema#comment";
        const string RdfsDomain = "http://www.w3.org/2000/01/rdf-schema#domain";
        const string RdfsRange = "http://www.w3.org/2000/01/rdf-schema#range";
        const string RdfsSubClassOf = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
        const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        foreach (var s in statements)
        {
            if (s.Subject is not RdfIri si) continue;
            var siri = si.Value;
            var piri = s.PredicateIri;

            if (piri == RdfType && s.Object is RdfIri oType)
            {
                if (oType.Value == OwlObjectProperty)
                    objectProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
                else if (oType.Value == OwlDatatypeProperty)
                    dataProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
                else if (oType.Value == OwlClass)
                    classes.TryAdd(siri, new OntologyClass(siri, Label: null));
            }
            else if (piri == RdfsDomain && s.Object is RdfIri d)
            {
                domains[siri] = d.Value;
            }
            else if (piri == RdfsRange && s.Object is RdfIri rn)
            {
                ranges[siri] = rn.Value;
            }
            else if (piri == RdfsLabel && s.Object is RdfLiteral lit)
            {
                labels[siri] = lit.Value;
            }
            else if (piri == RdfsComment && s.Object is RdfLiteral lit2)
            {
                comments[siri] = lit2.Value;
            }
            else if (piri == RdfsSubClassOf && s.Object is RdfIri sup)
            {
                subclassOf.Add(new SubclassAxiom(siri, sup.Value));
            }
            else if (piri == OwlDisjointWith && s.Object is RdfIri dj)
            {
                disjointWith.Add(new PairAxiom(siri, dj.Value));
            }
            else if (piri == OwlEquivalentClass && s.Object is RdfIri ec)
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
}