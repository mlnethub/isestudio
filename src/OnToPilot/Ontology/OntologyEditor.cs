using System.Collections;
using System.Text.Json;
using System.Threading;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

/// <summary>
/// Raised when an <see cref="OntologyEditor"/> op encounters an unknown
/// action or an invalid payload (missing required field, class not found,
/// unsupported axiom kind, …).
/// </summary>
public sealed class OntologyEditException : Exception
{
    public OntologyEditException(string message) : base(message) { }
    public OntologyEditException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// High-level mutation API on top of <see cref="StoreWrapper"/>. Each
/// operation runs inside a <see cref="StoreWrapper.CaptureAsync"/> so a
/// thrown exception reverts the RDF writes atomically; SQL callers are
/// responsible for rolling back the EF transaction around the <c>await using</c>
/// block, per the global constraint "failure paths revert RDF before SQL".
/// </summary>
/// <remarks>
/// Mirrors the Python <c>editor.apply_edit</c> dispatcher; the supported
/// actions are <c>add_class</c>, <c>update_class</c>, <c>delete_class</c>,
/// <c>add_property</c>, <c>update_property</c>, <c>delete_property</c>,
/// <c>add_axiom</c>, <c>delete_axiom</c>. Merge / union / sub-property
/// operations belong to a later task and are intentionally not here.
/// </remarks>
public sealed class OntologyEditor
{
    private readonly StoreWrapper? _store;

    // The store is optional so the contract-test factory (which registers
    // a null StoreWrapper when no RocksDB root is provisioned) can still
    // resolve this service. Edits no-op + return the requested IRI when
    // the store is null; the HTTP envelope still parses cleanly.
    public OntologyEditor(StoreWrapper? store)
    {
        _store = store;
    }

    /// <summary>
    /// Apply a single structured edit. The returned string is the canonical
    /// IRI of the resource that was created or modified. Throws
    /// <see cref="OntologyEditException"/> for unknown actions or validation
    /// errors. On any thrown exception, RDF writes for this operation are
    /// reverted via the capture lease.
    /// </summary>
    public async ValueTask<string> ApplyEditAsync(
        string graphIri,
        string baseIri,
        IReadOnlyDictionary<string, object?> op,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        ArgumentException.ThrowIfNullOrEmpty(baseIri);
        ArgumentNullException.ThrowIfNull(op);

        if (!op.TryGetValue("op", out var opNameObj) || opNameObj is not string opName)
        {
            throw new OntologyEditException("Edit op requires a string 'op' field.");
        }

        if (_store is null)
        {
            // No graph store wired (contract-test path) — skip the
            // capture / write pipeline and just compute the canonical
            // IRI the edit would have produced. Validation of unknown
            // ops / missing args still runs so the HTTP envelope shape
            // matches the live path.
            return ApplyEditNoStore(opName, baseIri, op);
        }

        // Every action goes through CaptureAsync so a thrown exception reverts
        // the RDF writes via MarkError(). We open with revertOnError=false
        // because the success branch commits the writes; the catch block
        // calls MarkError() to force revert on failure.
        await using var capture = await _store!.CaptureAsync(
            graphIri, revertOnError: false, cancellationToken: cancellationToken).ConfigureAwait(false);

        string result;
        try
        {
            result = opName switch
            {
                "add_class" => AddClass(graphIri, baseIri, op),
                "update_class" => UpdateClass(graphIri, baseIri, op),
                "delete_class" => DeleteClass(graphIri, baseIri, op),
                "add_property" => AddProperty(graphIri, baseIri, op),
                "update_property" => UpdateProperty(graphIri, baseIri, op),
                "delete_property" => DeleteProperty(graphIri, baseIri, op),
                "add_axiom" => AddAxiom(graphIri, baseIri, op),
                "delete_axiom" => DeleteAxiom(graphIri, baseIri, op),
                "set_property_union" => SetPropertyUnion(graphIri, baseIri, op),
                "merge_properties" => await MergePropertiesAsync(graphIri, baseIri, op).ConfigureAwait(false),
                "subordinate_properties" => await SubordinatePropertiesAsync(graphIri, baseIri, op).ConfigureAwait(false),
                _ => throw new OntologyEditException($"Unknown edit op: {opName}"),
            };
        }
        catch
        {
            capture.MarkError();
            throw;
        }
        return result;
    }

    // Mirror of the opName dispatch above for the null-store path. Computes
    // the canonical IRI (or axiom type) without touching the graph, so the
    // contract-test factory can still serve POST /ontology/edits without
    // throwing on a missing StoreWrapper.
    private static string ApplyEditNoStore(string opName, string baseIri, IReadOnlyDictionary<string, object?> op)
    {
        switch (opName)
        {
            case "add_class":
                {
                    var label = (GetString(op, "label") ?? "").Trim();
                    if (string.IsNullOrEmpty(label))
                        throw new OntologyEditException("label required");
                    return Vocabulary.ClassNode(baseIri, label).Value;
                }
            case "add_property":
                {
                    var label = (GetString(op, "label") ?? "").Trim();
                    if (string.IsNullOrEmpty(label))
                        throw new OntologyEditException("label required");
                    return Vocabulary.PropertyNode(baseIri, label).Value;
                }
            case "update_class":
            case "update_property":
            case "delete_class":
            case "delete_property":
                {
                    if (!op.TryGetValue("iri", out var iriObj) || iriObj is not string iri || string.IsNullOrEmpty(iri))
                        throw new OntologyEditException($"{opName} requires 'iri'");
                    return iri;
                }
            case "add_axiom":
            case "delete_axiom":
                {
                    if (!op.TryGetValue("type", out var tObj) || tObj is not string t)
                        throw new OntologyEditException($"{opName} requires 'type'");
                    return t;
                }
            case "set_property_union":
                {
                    var iri = GetString(op, "iri");
                    if (string.IsNullOrEmpty(iri))
                        throw new OntologyEditException("set_property_union requires 'iri'");
                    return iri;
                }
            case "merge_properties":
            case "subordinate_properties":
                {
                    // Validate sources present; return the target IRI (or
                    // a synthesized one from target_label) so the contract-
                    // test path returns a non-empty string envelope.
                    var srcList = ReadStringArray(op, "sources");
                    if (srcList.Count == 0)
                        throw new OntologyEditException($"{opName} requires 'sources'");
                    var tgt = GetString(op, "target") ?? GetString(op, "target_label") ?? srcList[0];
                    return tgt;
                }
            default:
                throw new OntologyEditException($"Unknown edit op: {opName}");
        }
    }

    // ------------------------------------------------------------------
    // ABox cascade helpers (Task 1 GraphWriteCoordinator supports per-graph
    // locks; captures on different named graphs are independent).
    // ------------------------------------------------------------------

    /// <summary>The instance graph paired with a TBox graph (mirrors Python <c>_abox_iri</c>).</summary>
    private static string AboxIri(string graphIri) =>
        graphIri.TrimEnd('/') + "/abox";

    /// <summary>
    /// Walk the paired ABox graph and rewrite any rdf:type to
    /// <paramref name="clsIri"/>. For individuals where the only type was
    /// the now-deleted class, remove the whole individual (with its
    /// assertions). Wrapped in its own capture so a thrown exception
    /// reverts the ABox writes; the outer capture reverts the TBox writes
    /// via <see cref="QuadChangeCapture.MarkError"/>.
    /// </summary>
    private async ValueTask CascadeClassDeleteAsync(string graphIri, string clsIri)
    {
        var aboxIri = AboxIri(graphIri);
        var aboxGraph = new OntoNamedNode(aboxIri);
        // Snapshot the ABox BEFORE taking the capture so we don't depend on
        // the outer capture being open for this graph (different locks).
        await using var aboxCapture = await _store!.CaptureAsync(
            aboxGraph, revertOnError: true).ConfigureAwait(false);

        var aboxQuads = _store!.Match(graph: aboxGraph);
        var typesByInd = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var q in aboxQuads)
        {
            if (q.Predicate.Value == Vocabulary.RdfType.Value && q.Object is OntoNamedNode o)
            {
                var subjIri = q.Subject switch
                {
                    OntoNamedNode n => n.Value,
                    OntoBlankNode b => b.Value,
                    _ => q.Subject.ToString() ?? "",
                };
                if (!typesByInd.TryGetValue(subjIri, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    typesByInd[subjIri] = set;
                }
                set.Add(o.Value);
            }
        }
        foreach (var (indIri, types) in typesByInd)
        {
            if (!types.Contains(clsIri)) continue;
            var remaining = new HashSet<string>(types, StringComparer.Ordinal);
            remaining.Remove(clsIri);
            remaining.Remove(Vocabulary.OwlNamedIndividual.Value);
            if (remaining.Count > 0)
            {
                var drop = _store!.Match(subjectIri: indIri, predicateIri: Vocabulary.RdfType.Value,
                    objectIri: clsIri, graphIri: aboxIri);
                if (drop.Count > 0) _store!.RemoveQuads(aboxGraph, drop);
            }
            else
            {
                var individualQuads = _store!.Match(subjectIri: indIri, graphIri: aboxIri);
                if (individualQuads.Count > 0) _store!.RemoveQuads(aboxGraph, individualQuads);
            }
        }
    }

    private async ValueTask CascadePropertyDeleteAsync(string graphIri, string propIri)
    {
        var aboxIri = AboxIri(graphIri);
        var aboxGraph = new OntoNamedNode(aboxIri);
        await using var aboxCapture = await _store!.CaptureAsync(
            aboxGraph, revertOnError: true).ConfigureAwait(false);

        var used = _store!.Match(predicateIri: propIri, graphIri: aboxIri);
        if (used.Count > 0) _store!.RemoveQuads(aboxGraph, used);
    }

    // The cascade takes its own capture on the ABox graph (a different
    // named graph, so a different lock) and must surface the 15s conflict
    // contract that GraphWriteCoordinator enforces on that capture. The
    // sync DeleteClass / DeleteProperty ops are invoked from inside
    // ApplyEditAsync's switch — blocking the calling thread on the
    // cascade's Task would either deadlock the synchronization context
    // (when ASP.NET Core pins the request thread) or needlessly block a
    // thread-pool worker (in test runs / console hosts). Pushing the
    // cascade onto a fresh thread-pool worker via Task.Run keeps the
    // wait genuinely synchronous from the caller's perspective without
    // starving the pool or capturing a SynchronizationContext.
    //
    // The async lambda is required so Task.Run picks the Func<Task>
    // overload — otherwise the compiler would pick Func<ValueTask>, and
    // the cascade's continuation (including the ABox capture acquisition
    // and any GraphWriteConflictException it raises) would never run.
    private void CascadeClassDelete(string graphIri, string clsIri) =>
        Task.Run(async () => await CascadeClassDeleteAsync(graphIri, clsIri))
            .GetAwaiter().GetResult();

    private void CascadePropertyDelete(string graphIri, string propIri) =>
        Task.Run(async () => await CascadePropertyDeleteAsync(graphIri, propIri))
            .GetAwaiter().GetResult();

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Make sure <paramref name="label"/> is a typed, labelled OWL class in
    /// <paramref name="graphIri"/>. Writes <c>rdf:type owl:Class</c> and
    /// <c>rdfs:label</c> only if the class has not already been declared.
    /// Returns the IRI of the class. Instance method: matches the Python
    /// <c>_ensure_labeled_class</c> which writes both triples.
    /// </summary>
    private OntoNamedNode EnsureLabeledClass(string graphIri, string baseIri, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var node = ClassNode(baseIri, label);
        var graph = new OntoNamedNode(graphIri);
        bool isNewClass = !_store!.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph));
        if (isNewClass)
        {
            _store!.AddQuads(graph, new[]
            {
                new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph),
            });
        }
        // rdfs:label is attached unless the caller passed an existing IRI
        // (which carries its own canonical label elsewhere) or the class
        // already has one in this graph.
        if (!IsIriRef(label))
        {
            bool alreadyLabelled = _store!.Match(
                subjectIri: node.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graphIri).Count > 0;
            if (!alreadyLabelled)
            {
                _store!.AddQuads(graph, new[]
                {
                    new OntoQuad(node, Vocabulary.RdfsLabel, new OntoLiteral(label), graph),
                });
            }
        }
        return node;
    }

    private static OntoNamedNode ClassNode(string baseIri, string refLabel)
    {
        if (refLabel.StartsWith("http://", StringComparison.Ordinal) || refLabel.StartsWith("https://", StringComparison.Ordinal))
        {
            return new OntoNamedNode(refLabel);
        }
        // The merge / dedup logic in build_mutation() needs an index of
        // existing classes; this simple version reuses Vocabulary.ClassNode.
        // A follow-up task wires the full MergeIndex lookup so label rename
        // history is honoured.
        return Vocabulary.ClassNode(baseIri, refLabel);
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    private string AddClass(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        var label = (GetString(p, "label") ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(label))
        {
            throw new OntologyEditException("label required");
        }
        var node = EnsureLabeledClass(graphIri, baseIri, label);
        var graph = new OntoNamedNode(graphIri);
        if (p.TryGetValue("comment", out var commentObj))
        {
            AddOrReplaceComment(node, graph, commentObj);
        }
        return node.Value;
    }

    private string UpdateClass(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        _ = baseIri;
        if (!p.TryGetValue("iri", out var iriObj) || iriObj is not string iri || string.IsNullOrEmpty(iri))
        {
            throw new OntologyEditException("update_class requires 'iri'");
        }
        var node = new OntoNamedNode(iri);
        var graph = new OntoNamedNode(graphIri);
        if (!_store!.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph)))
        {
            throw new OntologyEditException($"Class not found: {iri}");
        }
        if (p.TryGetValue("label", out var labelObj) && labelObj is string newLabel && !string.IsNullOrWhiteSpace(newLabel))
        {
            AddOrReplaceLabel(node, graph, newLabel);
        }
        if (p.TryGetValue("comment", out var commentObj))
        {
            AddOrReplaceComment(node, graph, commentObj);
        }
        return iri;
    }

    private string DeleteClass(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        _ = baseIri;
        if (!p.TryGetValue("iri", out var iriObj) || iriObj is not string iri || string.IsNullOrEmpty(iri))
        {
            throw new OntologyEditException("delete_class requires 'iri'");
        }
        var node = new OntoNamedNode(iri);
        var graph = new OntoNamedNode(graphIri);
        // Delete every quad whose subject is this class.
        var outgoing = _store!.Match(subjectIri: iri, graphIri: graphIri);
        if (outgoing.Count > 0)
        {
            _store!.RemoveQuads(graph, outgoing);
        }
        // And every quad where this class appears as the object.
        var incoming = _store!.Match(objectIri: iri, graphIri: graphIri);
        if (incoming.Count > 0)
        {
            _store!.RemoveQuads(graph, incoming);
        }
        // Cascade into the paired ABox graph: drop each instance's rdf:type
        // to this class; if that was the individual's only type, remove the
        // whole individual (with its assertions). Mirrors
        // _cascade_class_delete in backend/app/ontology/editor.py.
        CascadeClassDelete(graphIri, iri);
        return iri;
    }

    private string AddProperty(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        var label = (GetString(p, "label") ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(label))
        {
            throw new OntologyEditException("label required");
        }
        var kind = GetString(p, "kind") ?? "object";
        bool isObject = kind == "object";
        var node = Vocabulary.PropertyNode(baseIri, label);
        var graph = new OntoNamedNode(graphIri);
        var ptype = isObject ? Vocabulary.OwlObjectProperty : Vocabulary.OwlDatatypeProperty;
        _store!.AddQuads(graph, new[]
        {
            new OntoQuad(node, Vocabulary.RdfType, ptype, graph),
        });
        var existingLabel = _store!.Match(
            subjectIri: node.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graphIri);
        if (existingLabel.Count == 0)
        {
            _store!.AddQuads(graph, new[]
            {
                new OntoQuad(node, Vocabulary.RdfsLabel, new OntoLiteral(label), graph),
            });
        }
        if (p.TryGetValue("comment", out var commentObj))
        {
            AddOrReplaceComment(node, graph, commentObj);
        }
        if (p.TryGetValue("domain", out var domainObj) && domainObj is string domain && !string.IsNullOrWhiteSpace(domain))
        {
            var dnode = EnsureLabeledClass(graphIri, baseIri, domain);
            AddOrReplaceRangeTriple(node, graph, Vocabulary.RdfsDomain, dnode);
        }
        if (p.TryGetValue("range", out var rangeObj) && rangeObj is string range && !string.IsNullOrWhiteSpace(range))
        {
            var rng = isObject ? EnsureLabeledClass(graphIri, baseIri, range) : Vocabulary.DatatypeNode(range);
            AddOrReplaceRangeTriple(node, graph, Vocabulary.RdfsRange, rng);
        }
        return node.Value;
    }

    private string UpdateProperty(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        if (!p.TryGetValue("iri", out var iriObj) || iriObj is not string iri || string.IsNullOrEmpty(iri))
        {
            throw new OntologyEditException("update_property requires 'iri'");
        }
        var node = new OntoNamedNode(iri);
        var graph = new OntoNamedNode(graphIri);
        // Determine whether this property is currently an object or data property.
        bool isObjectProp = _store!.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlObjectProperty, graph));
        bool isDataProp = _store!.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlDatatypeProperty, graph));
        if (!isObjectProp && !isDataProp)
        {
            throw new OntologyEditException($"Property not found: {iri}");
        }
        bool isObject = isObjectProp;
        if (p.TryGetValue("label", out var labelObj) && labelObj is string newLabel && !string.IsNullOrWhiteSpace(newLabel))
        {
            AddOrReplaceLabel(node, graph, newLabel);
        }
        if (p.TryGetValue("comment", out var commentObj))
        {
            AddOrReplaceComment(node, graph, commentObj);
        }
        if (p.TryGetValue("clear_domain", out var clearD) && clearD is true)
        {
            var existing = _store!.Match(subjectIri: iri, predicateIri: Vocabulary.RdfsDomain.Value, graphIri: graphIri);
            if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        }
        else if (p.TryGetValue("domain", out var domainObj) && domainObj is string domain && !string.IsNullOrWhiteSpace(domain))
        {
            var dnode = EnsureLabeledClass(graphIri, baseIri, domain);
            AddOrReplaceRangeTriple(node, graph, Vocabulary.RdfsDomain, dnode);
        }
        if (p.TryGetValue("clear_range", out var clearR) && clearR is true)
        {
            var existing = _store!.Match(subjectIri: iri, predicateIri: Vocabulary.RdfsRange.Value, graphIri: graphIri);
            if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        }
        else if (p.TryGetValue("range", out var rangeObj) && rangeObj is string range && !string.IsNullOrWhiteSpace(range))
        {
            var rng = isObject ? EnsureLabeledClass(graphIri, baseIri, range) : Vocabulary.DatatypeNode(range);
            AddOrReplaceRangeTriple(node, graph, Vocabulary.RdfsRange, rng);
        }
        return iri;
    }

    private string DeleteProperty(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        _ = baseIri;
        if (!p.TryGetValue("iri", out var iriObj) || iriObj is not string iri || string.IsNullOrEmpty(iri))
        {
            throw new OntologyEditException("delete_property requires 'iri'");
        }
        var node = new OntoNamedNode(iri);
        var graph = new OntoNamedNode(graphIri);
        var outgoing = _store!.Match(subjectIri: iri, graphIri: graphIri);
        if (outgoing.Count > 0) _store!.RemoveQuads(graph, outgoing);
        // Cascade into the paired ABox graph: drop the instance assertions
        // that used this property as a predicate so they don't dangle on a
        // property that no longer exists.
        CascadePropertyDelete(graphIri, iri);
        return iri;
    }

    private string AddAxiom(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        if (!p.TryGetValue("type", out var tObj) || tObj is not string t)
        {
            throw new OntologyEditException("add_axiom requires 'type'");
        }
        var graph = new OntoNamedNode(graphIri);
        switch (t)
        {
            case "subclass":
                {
                    var sub = GetString(p, "sub");
                    var sup = GetString(p, "super");
                    if (string.IsNullOrWhiteSpace(sub) || string.IsNullOrWhiteSpace(sup))
                    {
                        throw new OntologyEditException("subclass requires 'sub' and 'super'");
                    }
                    var subNode = EnsureLabeledClass(graphIri, baseIri, sub);
                    var supNode = EnsureLabeledClass(graphIri, baseIri, sup);
                    if (subNode.Value != supNode.Value)
                    {
                        _store!.AddQuads(graph, new[]
                        {
                            new OntoQuad(subNode, Vocabulary.RdfsSubClassOf, supNode, graph),
                        });
                    }
                    return t;
                }
            case "disjoint":
            case "equivalent":
                {
                    var a = GetString(p, "a");
                    var b = GetString(p, "b");
                    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                    {
                        throw new OntologyEditException($"{t} requires 'a' and 'b'");
                    }
                    var aNode = EnsureLabeledClass(graphIri, baseIri, a);
                    var bNode = EnsureLabeledClass(graphIri, baseIri, b);
                    var pred = t == "disjoint" ? Vocabulary.OwlDisjointWith : Vocabulary.OwlEquivalentClass;
                    if (aNode.Value != bNode.Value)
                    {
                        _store!.AddQuads(graph, new[]
                        {
                            new OntoQuad(aNode, pred, bNode, graph),
                        });
                    }
                    return t;
                }
            default:
                throw new OntologyEditException($"Unknown axiom type: {t}");
        }
    }

    private string DeleteAxiom(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        _ = baseIri;
        if (!p.TryGetValue("type", out var tObj) || tObj is not string t)
        {
            throw new OntologyEditException("delete_axiom requires 'type'");
        }
        var graph = new OntoNamedNode(graphIri);
        switch (t)
        {
            case "subclass":
                {
                    var sub = GetString(p, "sub");
                    var sup = GetString(p, "super");
                    if (string.IsNullOrWhiteSpace(sub) || string.IsNullOrWhiteSpace(sup))
                    {
                        throw new OntologyEditException("subclass requires 'sub' and 'super'");
                    }
                    var existing = _store!.Match(
                        subjectIri: sub, predicateIri: Vocabulary.RdfsSubClassOf.Value, objectIri: sup, graphIri: graphIri);
                    if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
                    return t;
                }
            case "disjoint":
            case "equivalent":
                {
                    var a = GetString(p, "a");
                    var b = GetString(p, "b");
                    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                    {
                        throw new OntologyEditException($"{t} requires 'a' and 'b'");
                    }
                    var pred = t == "disjoint" ? Vocabulary.OwlDisjointWith : Vocabulary.OwlEquivalentClass;
                    var existingAB = _store!.Match(subjectIri: a, predicateIri: pred.Value, objectIri: b, graphIri: graphIri);
                    var existingBA = _store!.Match(subjectIri: b, predicateIri: pred.Value, objectIri: a, graphIri: graphIri);
                    var union = existingAB.Concat(existingBA).ToList();
                    if (union.Count > 0) _store!.RemoveQuads(graph, union);
                    return t;
                }
            default:
                throw new OntologyEditException($"Unknown axiom type: {t}");
        }
    }

    // ------------------------------------------------------------------
    // Property merge / union / subordinate ops (slice 9)
    // Mirrors backend/app/ontology/editor.py:_set_property_union /
    // _merge_properties / _subordinate_properties (lines 242-394).
    // ------------------------------------------------------------------

    /// <summary>
    /// Set a property's domain or range to an anonymous
    /// <c>owl:Class ; owl:unionOf ( C1 C2 … )</c> expression. Drops the
    /// old slot value first (GC-ing any previous union blank-node subgraph)
    /// so replacing a union range leaves no orphans. Mirrors Python
    /// <c>_set_property_union</c>.
    /// </summary>
    private string SetPropertyUnion(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        _ = baseIri;
        var iri = GetString(p, "iri");
        if (string.IsNullOrEmpty(iri))
            throw new OntologyEditException("set_property_union requires 'iri'");
        var slot = GetString(p, "slot") ?? "range";
        var pred = slot == "domain" ? Vocabulary.RdfsDomain : Vocabulary.RdfsRange;

        // Harvest member IRIs (may arrive as string[] from the JSON body or
        // as a JsonElement when deserialised by the dispatcher).
        var members = ReadStringArray(p, "members");
        if (members.Count < 2)
            throw new OntologyEditException("union needs at least two members");

        var node = new OntoNamedNode(iri);
        var graph = new OntoNamedNode(graphIri);

        // Drop the old slot values, GC-ing any previous union expression.
        var oldSlot = _store!.Match(
            subjectIri: iri, predicateIri: pred.Value, graphIri: graphIri);
        foreach (var q in oldSlot)
        {
            if (q.Object is OntoBlankNode bn) GcBlankNode(graph, bn.Value);
        }
        if (oldSlot.Count > 0) _store!.RemoveQuads(graph, oldSlot);

        // Build:  _:u a owl:Class ; owl:unionOf ( C1 C2 … ) .   node pred _:u .
        var union = FreshBlank("union");
        var cells = members.Select(_ => FreshBlank("cell")).ToList();
        var build = new List<OntoQuad>
        {
            new(union, Vocabulary.RdfType, Vocabulary.OwlClass, graph),
        };
        for (var k = 0; k < cells.Count; k++)
        {
            build.Add(new OntoQuad(cells[k], Vocabulary.RdfFirst,
                new OntoNamedNode(members[k]), graph));
            Oxigraph.ITerm rest = k + 1 < cells.Count ? cells[k + 1] : Vocabulary.RdfNil;
            build.Add(new OntoQuad(cells[k], Vocabulary.RdfRest, rest, graph));
        }
        build.Add(new OntoQuad(union, Vocabulary.OwlUnionOf, cells[0], graph));
        build.Add(new OntoQuad(node, pred, union, graph));
        _store!.AddQuads(graph, build);
        return iri;
    }

    /// <summary>
    /// Collapse over-specialized object properties (e.g. 拥有井 / 拥有计量站)
    /// into one general property (拥有): repoint every triple that uses a
    /// source AS ITS PREDICATE to the target, union the sources' domains /
    /// ranges onto the target, then drop the sources. The ABox graph is
    /// cascaded so instance assertions repoint to the surviving property.
    /// Mirrors Python <c>_merge_properties</c>.
    /// </summary>
    private async Task<string> MergePropertiesAsync(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        var srcVals = ReadStringArray(p, "sources");
        if (srcVals.Count == 0)
            throw new OntologyEditException("merge_properties needs sources");
        var srcSet = srcVals.ToHashSet(StringComparer.Ordinal);
        var target = ResolvePropertyTarget(graphIri, baseIri, p, srcSet);
        var graph = new OntoNamedNode(graphIri);

        var toAdd = new List<OntoQuad>();
        var toRemove = new List<OntoQuad>();
        var domains = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new HashSet<string>(StringComparer.Ordinal);

        // Sweep the TBox graph: repoint usage triples, harvest the sources'
        // domain/range, and drop the sources' own definition triples.
        foreach (var q in _store!.Match(graph: graph))
        {
            if (q.Predicate is not OntoNamedNode pr) continue;
            // A USAGE triple (source used as predicate) → repoint to target.
            if (srcSet.Contains(pr.Value))
            {
                toRemove.Add(q);
                toAdd.Add(new OntoQuad(q.Subject, target, q.Object, graph));
                continue;
            }
            if (q.Subject is OntoNamedNode s && srcSet.Contains(s.Value))
            {
                // A source property's own definition triple → drop, harvest d/r.
                toRemove.Add(q);
                if (pr.Value == Vocabulary.RdfsDomain.Value && q.Object is OntoNamedNode d)
                    domains.Add(d.Value);
                else if (pr.Value == Vocabulary.RdfsRange.Value && q.Object is OntoNamedNode r)
                    ranges.Add(r.Value);
            }
            else if (q.Object is OntoNamedNode o && srcSet.Contains(o.Value))
            {
                // Rare: a source referenced as an object — drop.
                toRemove.Add(q);
            }
        }
        if (toRemove.Count > 0) _store!.RemoveQuads(graph, toRemove);
        if (toAdd.Count > 0) _store!.AddQuads(graph, toAdd);

        // Repoint ABox assertions (object properties are used as predicates
        // in the paired ABox graph). Wrapped in its own capture.
        await CascadePropertyRepointAsync(graphIri, srcSet, target.Value).ConfigureAwait(false);

        UnionSlot(graphIri, baseIri, target, Vocabulary.RdfsDomain, "domain", domains);
        UnionSlot(graphIri, baseIri, target, Vocabulary.RdfsRange, "range", ranges);
        return target.Value;
    }

    /// <summary>
    /// Keep the specialized object properties but declare each
    /// <c>rdfs:subPropertyOf</c> a general one (created if needed), whose
    /// domain/range is the union of the sources'. Mirrors Python
    /// <c>_subordinate_properties</c>.
    /// </summary>
    private async Task<string> SubordinatePropertiesAsync(string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p)
    {
        var sources = ReadStringArray(p, "sources");
        if (sources.Count == 0)
            throw new OntologyEditException("subordinate_properties needs sources");
        var srcSet = sources.ToHashSet(StringComparer.Ordinal);
        var target = ResolvePropertyTarget(graphIri, baseIri, p, srcSet);
        var graph = new OntoNamedNode(graphIri);

        var domains = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new HashSet<string>(StringComparer.Ordinal);

        // Add rdfs:subPropertyOf target for each source, harvesting their
        // current domain/range into the union.
        foreach (var src in sources)
        {
            var node = new OntoNamedNode(src);
            foreach (var d in _store!.Match(
                subjectIri: src, predicateIri: Vocabulary.RdfsDomain.Value, graphIri: graphIri))
            {
                if (d.Object is OntoNamedNode dn) domains.Add(dn.Value);
            }
            foreach (var r in _store!.Match(
                subjectIri: src, predicateIri: Vocabulary.RdfsRange.Value, graphIri: graphIri))
            {
                if (r.Object is OntoNamedNode rn) ranges.Add(rn.Value);
            }
            _store!.AddQuads(graph, new[]
            {
                new OntoQuad(node, Vocabulary.RdfsSubPropertyOf, target, graph),
            });
        }
        // Subordinate does not repoint ABox assertions — the specialized
        // properties are kept. No cascade needed, but keep the async
        // signature for API symmetry with MergePropertiesAsync.
        await Task.CompletedTask.ConfigureAwait(false);

        UnionSlot(graphIri, baseIri, target, Vocabulary.RdfsDomain, "domain", domains);
        UnionSlot(graphIri, baseIri, target, Vocabulary.RdfsRange, "range", ranges);
        return target.Value;
    }

    /// <summary>
    /// Resolve / create the general target object property (by IRI or
    /// label). <paramref name="forbidden"/> (the source IRIs) is checked
    /// BEFORE any write, so a rejected merge / subordinate (target == a
    /// source) never mutates the graph. Mirrors Python <c>_prop_target</c>.
    /// </summary>
    private OntoNamedNode ResolvePropertyTarget(
        string graphIri, string baseIri, IReadOnlyDictionary<string, object?> p, HashSet<string> forbidden)
    {
        var label = (GetString(p, "target_label") ?? string.Empty).Trim();
        OntoNamedNode target;
        if (p.TryGetValue("target", out var tgtObj) && tgtObj is string tgt && !string.IsNullOrEmpty(tgt))
        {
            target = new OntoNamedNode(tgt);
        }
        else if (!string.IsNullOrEmpty(label))
        {
            target = Vocabulary.PropertyNode(baseIri, label);
        }
        else
        {
            throw new OntologyEditException("needs target or target_label");
        }
        if (forbidden.Contains(target.Value))
            throw new OntologyEditException("target cannot be one of the sources");

        var graph = new OntoNamedNode(graphIri);
        // Ensure the target is typed as an owl:ObjectProperty.
        _store!.AddQuads(graph, new[]
        {
            new OntoQuad(target, Vocabulary.RdfType, Vocabulary.OwlObjectProperty, graph),
        });
        if (!string.IsNullOrEmpty(label))
        {
            bool hasLabel = _store!.Match(
                subjectIri: target.Value, predicateIri: Vocabulary.RdfsLabel.Value,
                graphIri: graphIri).Count > 0;
            if (!hasLabel)
            {
                _store!.AddQuads(graph, new[]
                {
                    new OntoQuad(target, Vocabulary.RdfsLabel, new OntoLiteral(label), graph),
                });
            }
        }
        return target;
    }

    /// <summary>
    /// Set target's domain/range to its current values ∪
    /// <paramref name="collected"/> (union if 2+ members, single value
    /// otherwise). Mirrors Python <c>_union_slot</c>.
    /// </summary>
    private void UnionSlot(
        string graphIri, string baseIri, OntoNamedNode target, OntoNamedNode pred,
        string slot, HashSet<string> collected)
    {
        var cur = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in _store!.Match(
            subjectIri: target.Value, predicateIri: pred.Value, graphIri: graphIri))
        {
            if (q.Object is OntoNamedNode n) cur.Add(n.Value);
        }
        var members = new SortedSet<string>(cur, StringComparer.Ordinal);
        members.UnionWith(collected);
        if (members.Count == 0) return;
        if (members.Count == 1)
        {
            SetSingle(target, pred, new OntoNamedNode(members.First()), graphIri);
        }
        else
        {
            var unionOp = new Dictionary<string, object?>
            {
                ["op"] = "set_property_union",
                ["iri"] = target.Value,
                ["slot"] = slot,
                ["members"] = members.ToList(),
            };
            SetPropertyUnion(graphIri, baseIri, unionOp);
        }
    }

    /// <summary>
    /// Replace all values of a single-valued predicate on
    /// <paramref name="subject"/>. GCs any previous blank-node expression.
    /// Mirrors Python <c>_set_single</c>.
    /// </summary>
    private void SetSingle(OntoNamedNode subject, OntoNamedNode pred, OntoNamedNode? obj, string graphIri)
    {
        var graph = new OntoNamedNode(graphIri);
        var existing = _store!.Match(
            subjectIri: subject.Value, predicateIri: pred.Value, graphIri: graphIri);
        foreach (var q in existing)
        {
            if (q.Object is OntoBlankNode bn) GcBlankNode(graph, bn.Value);
        }
        if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        if (obj is not null)
        {
            _store!.AddQuads(graph, new[] { new OntoQuad(subject, pred, obj, graph) });
        }
    }

    /// <summary>
    /// Garbage-collect a blank-node expression and every blank node
    /// reachable from it (e.g. an <c>owl:unionOf</c> class and its RDF
    /// list), so replacing a union range leaves no orphans. Mirrors
    /// Python <c>_gc_blank</c>.
    /// </summary>
    private void GcBlankNode(OntoNamedNode graph, string bnodeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(bnodeId);
        var toRemove = new List<OntoQuad>();
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            foreach (var q in _store!.Match(graph: graph))
            {
                if (q.Subject is OntoBlankNode s && s.Value == cur)
                {
                    toRemove.Add(q);
                    if (q.Object is OntoBlankNode ob) stack.Push(ob.Value);
                }
            }
        }
        if (toRemove.Count > 0) _store!.RemoveQuads(graph, toRemove);
    }

    /// <summary>
    /// Walk the paired ABox graph and repoint every assertion whose
    /// predicate is one of <paramref name="sourceIris"/> to
    /// <paramref name="targetIri"/>. Wrapped in its own capture so a
    /// thrown exception reverts the ABox writes; the outer capture
    /// reverts the TBox writes via MarkError.
    /// </summary>
    private async ValueTask CascadePropertyRepointAsync(
        string graphIri, HashSet<string> sourceIris, string targetIri)
    {
        var aboxIri = AboxIri(graphIri);
        var aboxGraph = new OntoNamedNode(aboxIri);
        // revertOnError:false so the repoint persists on success; the
        // catch calls MarkError() to force revert on failure. Mirrors
        // the outer ApplyEditAsync capture pattern.
        await using var aboxCapture = await _store!.CaptureAsync(
            aboxGraph, revertOnError: false).ConfigureAwait(false);

        try
        {
            var toAdd = new List<OntoQuad>();
            var toRemove = new List<OntoQuad>();
            var target = new OntoNamedNode(targetIri);
            foreach (var q in _store!.Match(graph: aboxGraph))
            {
                if (q.Predicate is OntoNamedNode pr && sourceIris.Contains(pr.Value))
                {
                    toRemove.Add(q);
                    toAdd.Add(new OntoQuad(q.Subject, target, q.Object, aboxGraph));
                }
            }
            if (toRemove.Count > 0) _store!.RemoveQuads(aboxGraph, toRemove);
            if (toAdd.Count > 0) _store!.AddQuads(aboxGraph, toAdd);
        }
        catch
        {
            aboxCapture.MarkError();
            throw;
        }
    }

    /// <summary>
    /// Read a string array field from the op dict. Handles both
    /// <c>string[]</c> (dispatcher body) and <c>JsonElement</c>
    /// (JSON-deserialised) representations.
    /// </summary>
    private static List<string> ReadStringArray(IReadOnlyDictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return new();
        if (v is string[] arr) return arr.Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var e in je.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    var s = e.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }
        // JsonElementToObject converts arrays to List<object?> — iterate
        // and keep the string elements. Also covers List<string> via
        // covariance (IEnumerable<string> is IEnumerable<object?>).
        if (v is IEnumerable<object?> objs)
        {
            var result = new List<string>();
            foreach (var o in objs)
            {
                if (o is string s && !string.IsNullOrEmpty(s))
                    result.Add(s);
            }
            return result;
        }
        // Fallback: older conflict rows may have stored the array as raw
        // JSON text (pre-JsonValueKind.Array fix in JsonElementToObject).
        // Parse it back into a string list.
        if (v is string json && json.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var result = new List<string>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            var s = e.GetString();
                            if (!string.IsNullOrEmpty(s)) result.Add(s);
                        }
                    }
                }
                return result;
            }
            catch { /* not valid JSON — fall through to empty */ }
        }
        return new();
    }

    /// <summary>Generate a fresh blank node with a unique ID.</summary>
    private OntoBlankNode FreshBlank(string prefix) =>
        new($"{prefix}_{Interlocked.Increment(ref _blankSeq)}");

    private int _blankSeq;


    // ------------------------------------------------------------------

    private void AddOrReplaceLabel(OntoNamedNode subject, OntoNamedNode graph, string label)
    {
        var existing = _store!.Match(
            subjectIri: subject.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        _store!.AddQuads(graph, new[]
        {
            new OntoQuad(subject, Vocabulary.RdfsLabel, new OntoLiteral(label), graph),
        });
    }

    private void AddOrReplaceComment(OntoNamedNode subject, OntoNamedNode graph, object? value)
    {
        var existing = _store!.Match(
            subjectIri: subject.Value, predicateIri: Vocabulary.RdfsComment.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            _store!.AddQuads(graph, new[]
            {
                new OntoQuad(subject, Vocabulary.RdfsComment, new OntoLiteral(s), graph),
            });
        }
    }

    private void AddOrReplaceRangeTriple(OntoNamedNode subject, OntoNamedNode graph, OntoNamedNode predicate, object objValue)
    {
        var existing = _store!.Match(
            subjectIri: subject.Value, predicateIri: predicate.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store!.RemoveQuads(graph, existing);
        if (objValue is OntoNamedNode n)
        {
            _store!.AddQuads(graph, new[]
            {
                new OntoQuad(subject, predicate, n, graph),
            });
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> p, string key) =>
        p.TryGetValue(key, out var v) && v is string s ? s : null;

    private static bool IsIriRef(string s) =>
        s.StartsWith("http://", StringComparison.Ordinal) || s.StartsWith("https://", StringComparison.Ordinal);
}