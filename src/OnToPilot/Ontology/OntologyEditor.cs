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
    private readonly StoreWrapper _store;

    public OntologyEditor(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
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

        // Every action goes through CaptureAsync so a thrown exception reverts
        // the RDF writes via MarkError(). We open with revertOnError=false
        // because the success branch commits the writes; the catch block
        // calls MarkError() to force revert on failure.
        await using var capture = await _store.CaptureAsync(
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
        await using var aboxCapture = await _store.CaptureAsync(
            aboxGraph, revertOnError: true).ConfigureAwait(false);

        var aboxQuads = _store.Match(graph: aboxGraph);
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
                var drop = _store.Match(subjectIri: indIri, predicateIri: Vocabulary.RdfType.Value,
                    objectIri: clsIri, graphIri: aboxIri);
                if (drop.Count > 0) _store.RemoveQuads(aboxGraph, drop);
            }
            else
            {
                var individualQuads = _store.Match(subjectIri: indIri, graphIri: aboxIri);
                if (individualQuads.Count > 0) _store.RemoveQuads(aboxGraph, individualQuads);
            }
        }
    }

    private async ValueTask CascadePropertyDeleteAsync(string graphIri, string propIri)
    {
        var aboxIri = AboxIri(graphIri);
        var aboxGraph = new OntoNamedNode(aboxIri);
        await using var aboxCapture = await _store.CaptureAsync(
            aboxGraph, revertOnError: true).ConfigureAwait(false);

        var used = _store.Match(predicateIri: propIri, graphIri: aboxIri);
        if (used.Count > 0) _store.RemoveQuads(aboxGraph, used);
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
        bool isNewClass = !_store.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph));
        if (isNewClass)
        {
            _store.AddQuads(graph, new[]
            {
                new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph),
            });
        }
        // rdfs:label is attached unless the caller passed an existing IRI
        // (which carries its own canonical label elsewhere) or the class
        // already has one in this graph.
        if (!IsIriRef(label))
        {
            bool alreadyLabelled = _store.Match(
                subjectIri: node.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graphIri).Count > 0;
            if (!alreadyLabelled)
            {
                _store.AddQuads(graph, new[]
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
        if (!_store.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph)))
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
        var outgoing = _store.Match(subjectIri: iri, graphIri: graphIri);
        if (outgoing.Count > 0)
        {
            _store.RemoveQuads(graph, outgoing);
        }
        // And every quad where this class appears as the object.
        var incoming = _store.Match(objectIri: iri, graphIri: graphIri);
        if (incoming.Count > 0)
        {
            _store.RemoveQuads(graph, incoming);
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
        _store.AddQuads(graph, new[]
        {
            new OntoQuad(node, Vocabulary.RdfType, ptype, graph),
        });
        var existingLabel = _store.Match(
            subjectIri: node.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graphIri);
        if (existingLabel.Count == 0)
        {
            _store.AddQuads(graph, new[]
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
        bool isObjectProp = _store.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlObjectProperty, graph));
        bool isDataProp = _store.ContainsQuad(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlDatatypeProperty, graph));
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
            var existing = _store.Match(subjectIri: iri, predicateIri: Vocabulary.RdfsDomain.Value, graphIri: graphIri);
            if (existing.Count > 0) _store.RemoveQuads(graph, existing);
        }
        else if (p.TryGetValue("domain", out var domainObj) && domainObj is string domain && !string.IsNullOrWhiteSpace(domain))
        {
            var dnode = EnsureLabeledClass(graphIri, baseIri, domain);
            AddOrReplaceRangeTriple(node, graph, Vocabulary.RdfsDomain, dnode);
        }
        if (p.TryGetValue("clear_range", out var clearR) && clearR is true)
        {
            var existing = _store.Match(subjectIri: iri, predicateIri: Vocabulary.RdfsRange.Value, graphIri: graphIri);
            if (existing.Count > 0) _store.RemoveQuads(graph, existing);
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
        var outgoing = _store.Match(subjectIri: iri, graphIri: graphIri);
        if (outgoing.Count > 0) _store.RemoveQuads(graph, outgoing);
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
                        _store.AddQuads(graph, new[]
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
                        _store.AddQuads(graph, new[]
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
                    var existing = _store.Match(
                        subjectIri: sub, predicateIri: Vocabulary.RdfsSubClassOf.Value, objectIri: sup, graphIri: graphIri);
                    if (existing.Count > 0) _store.RemoveQuads(graph, existing);
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
                    var existingAB = _store.Match(subjectIri: a, predicateIri: pred.Value, objectIri: b, graphIri: graphIri);
                    var existingBA = _store.Match(subjectIri: b, predicateIri: pred.Value, objectIri: a, graphIri: graphIri);
                    var union = existingAB.Concat(existingBA).ToList();
                    if (union.Count > 0) _store.RemoveQuads(graph, union);
                    return t;
                }
            default:
                throw new OntologyEditException($"Unknown axiom type: {t}");
        }
    }

    // ------------------------------------------------------------------
    // Triple write helpers
    // ------------------------------------------------------------------

    private void AddOrReplaceLabel(OntoNamedNode subject, OntoNamedNode graph, string label)
    {
        var existing = _store.Match(
            subjectIri: subject.Value, predicateIri: Vocabulary.RdfsLabel.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store.RemoveQuads(graph, existing);
        _store.AddQuads(graph, new[]
        {
            new OntoQuad(subject, Vocabulary.RdfsLabel, new OntoLiteral(label), graph),
        });
    }

    private void AddOrReplaceComment(OntoNamedNode subject, OntoNamedNode graph, object? value)
    {
        var existing = _store.Match(
            subjectIri: subject.Value, predicateIri: Vocabulary.RdfsComment.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store.RemoveQuads(graph, existing);
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            _store.AddQuads(graph, new[]
            {
                new OntoQuad(subject, Vocabulary.RdfsComment, new OntoLiteral(s), graph),
            });
        }
    }

    private void AddOrReplaceRangeTriple(OntoNamedNode subject, OntoNamedNode graph, OntoNamedNode predicate, object objValue)
    {
        var existing = _store.Match(
            subjectIri: subject.Value, predicateIri: predicate.Value, graphIri: graph.Value);
        if (existing.Count > 0) _store.RemoveQuads(graph, existing);
        if (objValue is OntoNamedNode n)
        {
            _store.AddQuads(graph, new[]
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