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
        // Empty-graph path: no triples → empty envelope. Tasks 3-5
        // extend this with the full Python build_view algorithm.
        using var iter = quads.GetEnumerator();
        if (!iter.MoveNext()) return EmptyResponse();
        // Single triple or more: defer to Task 5 which fully populates.
        _ = iter;
        return EmptyResponse();
    }

    private static IEnumerable<Oxigraph.Quad> ParseNQuads(byte[] shard)
    {
        return Array.Empty<Oxigraph.Quad>();
    }
}