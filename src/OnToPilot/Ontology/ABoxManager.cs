using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

/// <summary>
/// ABox (instance) layer. Mirrors the Python <c>backend/app/ontology/abox.py</c>.
/// Each knowledge system keeps its instances in a separate named graph
/// (<see cref="KsContext.ABoxGraph"/>) so the much-larger instance dataset
/// scales independently of the TBox schema.
/// </summary>
/// <remarks>
/// <para>Mutation methods are synchronous wrappers around <see cref="StoreWrapper"/>
/// primitives; callers that want atomic revert on failure must wrap the call
/// in a <see cref="StoreWrapper.CaptureAsync(string, bool, TimeSpan?, CancellationToken)"/>
/// block (or pass <c>revertOnError:true</c> to a nested capture).</para>
/// <para>IRIs are minted from <see cref="KsContext.BaseIri"/> with a uuid4
/// suffix; the caller-supplied "individual IRI" argument is treated as a
/// label / display hint and is never echoed back as the IRI, matching the
/// Python <c>mint_iri</c> contract.</para>
/// </remarks>
public sealed class ABoxManager
{
    private readonly StoreWrapper _store;

    public ABoxManager(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    // ------------------------------------------------------------------
    // Individuals
    // ------------------------------------------------------------------

    /// <summary>
    /// Create a fresh individual in the ABox graph. Returns the minted IRI.
    /// <paramref name="individualIri"/> is treated as a label / hint and is
    /// not echoed back; the actual IRI is <c>BaseIri + "ind-" + uuid4[:12]</c>.
    /// </summary>
    public string CreateIndividual(KsContext ks, string individualIri, string classIri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(individualIri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var iri = MintIri(ks.BaseIri);
        var clsNode = new OntoNamedNode(classIri);
        var indNode = new OntoNamedNode(iri);

        var quads = new[]
        {
            new OntoQuad(indNode, Vocabulary.RdfType, Vocabulary.OwlNamedIndividual, aboxGraph),
            new OntoQuad(indNode, Vocabulary.RdfType, clsNode, aboxGraph),
        };
        _store.AddQuads(aboxGraph, quads);
        return iri;
    }

    /// <summary>Remove every quad whose subject is <paramref name="iri"/> in the ABox graph.</summary>
    public int DeleteIndividual(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var outgoing = _store.Match(subjectIri: iri, graphIri: ks.ABoxGraph);
        if (outgoing.Count == 0) return 0;
        _store.RemoveQuads(aboxGraph, outgoing);
        return outgoing.Count;
    }

    /// <summary>Add <c>iri rdf:type classIri</c> to the ABox graph.</summary>
    public void AddType(KsContext ks, string iri, string classIri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        _store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(
                new OntoNamedNode(iri),
                Vocabulary.RdfType,
                new OntoNamedNode(classIri),
                aboxGraph),
        });
    }

    /// <summary>Remove the <c>iri rdf:type classIri</c> triple from the ABox graph.</summary>
    public void RemoveType(KsContext ks, string iri, string classIri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var existing = _store.Match(
            subjectIri: iri,
            predicateIri: Vocabulary.RdfType.Value,
            objectIri: classIri,
            graphIri: ks.ABoxGraph);
        if (existing.Count > 0)
        {
            _store.RemoveQuads(aboxGraph, existing);
        }
    }

    // ------------------------------------------------------------------
    // Assertions
    // ------------------------------------------------------------------

    /// <summary>
    /// Add an object-property assertion <c>(s p o)</c>. Returns
    /// <c>false</c> if the exact triple is already present (caller can use
    /// this to count only fresh assertions).
    /// </summary>
    public bool AddObjectAssertion(KsContext ks, string subject, string property, string target)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var s = new OntoNamedNode(subject);
        var p = new OntoNamedNode(property);
        var o = new OntoNamedNode(target);
        var existing = _store.Match(subjectIri: subject, predicateIri: property, objectIri: target, graphIri: ks.ABoxGraph);
        if (existing.Count > 0) return false;
        _store.AddQuads(aboxGraph, new[] { new OntoQuad(s, p, o, aboxGraph) });
        return true;
    }

    /// <summary>Remove an object-property assertion.</summary>
    public void RemoveObjectAssertion(KsContext ks, string subject, string property, string target)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var existing = _store.Match(
            subjectIri: subject, predicateIri: property, objectIri: target, graphIri: ks.ABoxGraph);
        if (existing.Count > 0) _store.RemoveQuads(aboxGraph, existing);
    }

    /// <summary>
    /// Add a data-property assertion <c>(s p "value"^^dt)</c>. <paramref name="datatype"/>
    /// is optional; when <c>null</c> the literal has no explicit datatype (which
    /// means <c>xsd:string</c> per the RDF spec).
    /// </summary>
    public bool AddDataAssertion(KsContext ks, string subject, string property, string value, string? datatype)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var literal = datatype is null
            ? new OntoLiteral(value)
            : new OntoLiteral(value, Datatype: new OntoNamedNode(datatype));

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property, graphIri: ks.ABoxGraph);
        foreach (var q in existing)
        {
            if (q.Object is OntoLiteral l
                && l.Value == literal.Value
                && ((l.Datatype?.Value) == (literal.Datatype?.Value)))
            {
                return false;
            }
        }
        _store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(new OntoNamedNode(subject), new OntoNamedNode(property), literal, aboxGraph),
        });
        return true;
    }

    /// <summary>Remove a data-property assertion.</summary>
    public void RemoveDataAssertion(KsContext ks, string subject, string property, string value, string? datatype)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var literal = datatype is null
            ? new OntoLiteral(value)
            : new OntoLiteral(value, Datatype: new OntoNamedNode(datatype));

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property, graphIri: ks.ABoxGraph);
        foreach (var q in existing)
        {
            if (q.Object is OntoLiteral l && l.Value == literal.Value)
            {
                _store.RemoveQuads(aboxGraph, new[] { q });
                return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------

    /// <summary>Every triple in the ABox graph.</summary>
    public IReadOnlyList<OntoQuad> All(KsContext ks) =>
        _store.Match(graph: new OntoNamedNode(ks.ABoxGraph));

    /// <summary>
    /// A flat <c>iri -&gt; label</c> map for every individual in the ABox
    /// graph, built from a single scan.
    /// </summary>
    public IReadOnlyDictionary<string, string> LabelIndex(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var out_ = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in All(ks))
        {
            if (q.Subject is OntoNamedNode n
                && q.Predicate.Value == Vocabulary.RdfsLabel.Value
                && q.Object is OntoLiteral l)
            {
                out_[n.Value] = l.Value;
            }
        }
        return out_;
    }

    /// <summary>Whether any triple exists whose subject is <paramref name="iri"/>.</summary>
    public bool Exists(KsContext ks, string iri) =>
        _store.Match(subjectIri: iri, graphIri: ks.ABoxGraph).Count > 0;

    /// <summary>
    /// Returns every individual IRI in the ABox graph — defined as every
    /// subject that has at least one <c>rdf:type</c> triple.
    /// </summary>
    public IReadOnlyList<string> ListIndividuals(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in All(ks))
        {
            if (q.Subject is OntoNamedNode n
                && q.Predicate.Value == Vocabulary.RdfType.Value)
            {
                subjects.Add(n.Value);
            }
        }
        return subjects.ToList();
    }

    /// <summary>
    /// Mint a fresh individual IRI: <c>BaseIri + "ind-" + uuid4[:12]</c>.
    /// Mirrors Python <c>mint_iri</c>.
    /// </summary>
    public static string MintIri(string baseIri)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseIri);
        return $"{baseIri}ind-{Guid.NewGuid().ToString("N")[..12]}";
    }
}