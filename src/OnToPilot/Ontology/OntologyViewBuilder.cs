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
        // V1: classes + superclasses. Tasks 4-5 add properties / axioms / labels / stats.

        var classes = new Dictionary<string, OntologyClass>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var subclassOf = new List<SubclassAxiom>();

        const string OwlClass = "http://www.w3.org/2002/07/owl#Class";
        const string RdfsLabel = "http://www.w3.org/2000/01/rdf-schema#label";
        const string RdfsComment = "http://www.w3.org/2000/01/rdf-schema#comment";
        const string RdfsSubClassOf = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
        const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

        foreach (var q in quads)
        {
            if (q.Subject is not Oxigraph.NamedNode s) continue;
            if (q.Predicate is not Oxigraph.NamedNode p) continue;
            var siri = s.Value;
            var piri = p.Value;

            if (piri == RdfType
                && q.Object is Oxigraph.NamedNode o
                && o.Value == OwlClass)
            {
                classes.TryAdd(siri, new OntologyClass(siri, Label: null));
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
        }

        var superBySub = subclassOf
            .GroupBy(a => a.Sub, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.Super).ToList(),
                StringComparer.Ordinal);

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

        return new OntologyResponse(
            Classes: classList,
            ObjectProperties: Array.Empty<OntologyProperty>(),
            DataProperties: Array.Empty<OntologyProperty>(),
            Axioms: new OntologyAxioms(
                SubclassOf: subclassOf,
                DisjointWith: Array.Empty<PairAxiom>(),
                EquivalentClass: Array.Empty<PairAxiom>()),
            Labels: labels,
            Stats: new OntologyStats(classList.Count, 0, subclassOf.Count),
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
        return Array.Empty<Oxigraph.Quad>();
    }
}