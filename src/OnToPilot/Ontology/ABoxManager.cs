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
    private readonly StoreWrapper? _store;

    // The store is optional so the contract-test factory (which registers
    // a null StoreWrapper when no RocksDB root is provisioned) can still
    // resolve this service. Read methods return empty results and write
    // methods no-op when the store is null; the public contract shape is
    // preserved so the HTTP endpoints respond cleanly.
    public ABoxManager(StoreWrapper? store)
    {
        _store = store;
    }

    // ------------------------------------------------------------------
    // Individuals
    // ------------------------------------------------------------------

    /// <summary>
    /// Create a fresh individual in the ABox graph. Returns the minted IRI.
    /// <paramref name="label"/> is written as an <c>rdfs:label</c> triple so
    /// the read APIs can echo a human-readable name (matches Python
    /// <c>abox.create_individual</c>); <paramref name="individualIri"/> is
    /// a hint only — the actual IRI is <c>BaseIri + "ind-" + uuid4[:12]</c>.
    /// </summary>
    public string CreateIndividual(KsContext ks, string individualIri, string classIri, string label)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(individualIri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);
        ArgumentNullException.ThrowIfNull(label);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var iri = MintIri(ks.BaseIri);

        if (_store is null)
        {
            // No graph store wired (contract-test path) — mint the IRI
            // without persisting so the HTTP envelope still parses.
            return iri;
        }

        var clsNode = new OntoNamedNode(classIri);
        var indNode = new OntoNamedNode(iri);

        var quads = new List<OntoQuad>(3)
        {
            new(indNode, Vocabulary.RdfType, Vocabulary.OwlNamedIndividual, aboxGraph),
            new(indNode, Vocabulary.RdfType, clsNode, aboxGraph),
        };
        if (label.Length > 0)
        {
            quads.Add(new OntoQuad(
                indNode,
                Vocabulary.RdfsLabel,
                new OntoLiteral(label),
                aboxGraph));
        }
        _store.AddQuads(aboxGraph, quads);
        return iri;
    }

    /// <summary>
    /// Convenience overload that preserves the original call shape for
    /// callers that don't yet supply a label (the existing unit tests
    /// and the extraction seed loop assume "no rdfs:label" so the
    /// caller can decide the label on a separate triple). New
    /// user-facing callers should pass the 4-arg overload with a label.
    /// </summary>
    public string CreateIndividual(KsContext ks, string individualIri, string classIri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(individualIri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var iri = MintIri(ks.BaseIri);

        if (_store is null)
        {
            // No graph store wired (contract-test path) — mint the IRI
            // without persisting so the HTTP envelope still parses.
            return iri;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — nothing to remove.
            return 0;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — nothing to write.
            return;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — nothing to remove.
            return;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — report the
            // assertion as already-present so the caller doesn't count
            // it as a fresh write.
            return false;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — nothing to remove.
            return;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — report the
            // assertion as already-present so the caller doesn't count
            // it as a fresh write.
            return false;
        }

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

        if (_store is null)
        {
            // No graph store wired (contract-test path) — nothing to remove.
            return;
        }

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
        _store is null
            ? Array.Empty<OntoQuad>()
            : _store.Match(graph: new OntoNamedNode(ks.ABoxGraph));

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
        _store is not null
            && _store.Match(subjectIri: iri, graphIri: ks.ABoxGraph).Count > 0;

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
    /// Per-class individual counts across the ABox graph. Walks every
    /// <c>(s rdf:type cls)</c> triple once and tallies; falls back to
    /// zero for TBox classes that have no instances. Mirrors Python
    /// <c>abox.counts_by_class</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> CountsByClass(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in All(ks))
        {
            if (q.Subject is OntoNamedNode
                && q.Predicate.Value == Vocabulary.RdfType.Value
                && q.Object is OntoNamedNode cls
                && cls.Value != Vocabulary.OwlNamedIndividual.Value)
            {
                counts[cls.Value] = counts.TryGetValue(cls.Value, out var n) ? n + 1 : 1;
            }
        }
        return counts;
    }

    /// <summary>
    /// Project one row of the <c>/abox/individuals</c> listing — IRIs,
    /// the human-readable label (or local name fallback), and the
    /// classes the individual declares.
    /// </summary>
    public IReadOnlyList<IndividualListItem> ListIndividualsPaged(
        KsContext ks,
        IReadOnlyDictionary<string, string> classLabels,
        string? classIri,
        string? q,
        int offset,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(classLabels);

        // Build (subject → set of classIris, subject → label) from a single scan.
        var classBySubject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var labelBySubject = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var quad in All(ks))
        {
            if (quad.Subject is not OntoNamedNode subj) continue;
            if (quad.Predicate.Value == Vocabulary.RdfType.Value
                && quad.Object is OntoNamedNode cls)
            {
                if (!classBySubject.TryGetValue(subj.Value, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    classBySubject[subj.Value] = set;
                }
                set.Add(cls.Value);
            }
            else if (quad.Predicate.Value == Vocabulary.RdfsLabel.Value
                && quad.Object is OntoLiteral lit)
            {
                labelBySubject[subj.Value] = lit.Value;
            }
        }

        var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var filtered = classBySubject.Keys
            .Where(s => classIri is null || classBySubject[s].Contains(classIri))
            .Where(s =>
            {
                if (needle is null) return true;
                if (labelBySubject.TryGetValue(s, out var lbl)
                    && lbl.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return s.Contains(needle, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(s => labelBySubject.TryGetValue(s, out var l) ? l : LocalIri(s),
                StringComparer.Ordinal)
            .ToList();

        var page = filtered.Skip(offset).Take(limit).ToList();
        var items = new List<IndividualListItem>(page.Count);
        foreach (var iri in page)
        {
            var label = labelBySubject.TryGetValue(iri, out var l)
                ? l
                : LocalIri(iri);
            var types = classBySubject[iri]
                .Where(t => t != Vocabulary.OwlNamedIndividual.Value)
                .Select(t => t)
                .ToList();
            items.Add(new IndividualListItem(iri, label, types));
        }
        return items;
    }

    /// <summary>
    /// Build the <c>/abox/individuals</c> total count matching
    /// <see cref="ListIndividualsPaged"/>'s filter (without pagination).
    /// </summary>
    public int CountIndividualsPaged(
        KsContext ks,
        string? classIri,
        string? q)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var classBySubject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var labelBySubject = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var quad in All(ks))
        {
            if (quad.Subject is not OntoNamedNode subj) continue;
            if (quad.Predicate.Value == Vocabulary.RdfType.Value
                && quad.Object is OntoNamedNode cls)
            {
                if (!classBySubject.TryGetValue(subj.Value, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    classBySubject[subj.Value] = set;
                }
                set.Add(cls.Value);
            }
            else if (quad.Predicate.Value == Vocabulary.RdfsLabel.Value
                && quad.Object is OntoLiteral lit)
            {
                labelBySubject[subj.Value] = lit.Value;
            }
        }
        var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var total = classBySubject.Keys
            .Count(s => (classIri is null || classBySubject[s].Contains(classIri))
                && (needle is null
                    || (labelBySubject.TryGetValue(s, out var l)
                        && l.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    || s.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        return total;
    }

    /// <summary>
    /// Read the full individual envelope (types + object + data
    /// assertions). Returns <c>null</c> when <paramref name="iri"/> has
    /// no triples in the ABox graph. Mirrors Python
    /// <c>abox.get_individual</c> minus the per-fact <c>sources</c>
    /// attachment (deferred to the ABoxProvenanceService wire-up).
    /// </summary>
    public IndividualOut? GetIndividual(
        KsContext ks,
        string iri,
        IReadOnlyDictionary<string, string> classLabels,
        IReadOnlyDictionary<string, string> propLabels)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentNullException.ThrowIfNull(classLabels);
        ArgumentNullException.ThrowIfNull(propLabels);

        if (_store is null)
        {
            // No graph store wired (contract-test path) — mirror the
            // empty-store semantics so callers see a "not found"
            // envelope rather than a crash.
            return null;
        }

        var outgoing = _store.Match(subjectIri: iri, graphIri: ks.ABoxGraph);
        if (outgoing.Count == 0) return null;

        var types = new List<LabeledIri>();
        var objectAssertions = new List<ObjectAssertionOut>();
        var dataAssertions = new List<DataAssertionOut>();
        string? label = null;

        foreach (var quad in outgoing)
        {
            if (quad.Predicate.Value == Vocabulary.RdfType.Value
                && quad.Object is OntoNamedNode cls)
            {
                var clsIri = cls.Value;
                if (clsIri == Vocabulary.OwlNamedIndividual.Value) continue;
                types.Add(new LabeledIri(clsIri,
                    classLabels.TryGetValue(clsIri, out var l) ? l : LocalIri(clsIri)));
            }
            else if (quad.Predicate.Value == Vocabulary.RdfsLabel.Value
                && quad.Object is OntoLiteral labelLit)
            {
                label = labelLit.Value;
            }
            else if (quad.Object is OntoNamedNode target)
            {
                var propIri = quad.Predicate.Value;
                objectAssertions.Add(new ObjectAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Target: target.Value,
                    TargetLabel: LocalIri(target.Value),
                    Sources: Array.Empty<string>()));
            }
            else if (quad.Object is OntoLiteral literal)
            {
                var propIri = quad.Predicate.Value;
                dataAssertions.Add(new DataAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Value: literal.Value,
                    Datatype: literal.Datatype?.Value,
                    Sources: Array.Empty<string>()));
            }
        }

        return new IndividualOut(
            Iri: iri,
            Label: label ?? LocalIri(iri),
            Types: types,
            ObjectAssertions: objectAssertions,
            DataAssertions: dataAssertions);
    }

    /// <summary>
    /// Compute the local-name fragment of an IRI — the substring after
    /// the last <c>#</c> or last <c>/</c>. Mirrors the Python
    /// <c>local_name</c> helper that powers the sidebar fallback label.
    /// </summary>
    public static string LocalIri(string iri)
    {
        if (string.IsNullOrEmpty(iri)) return string.Empty;
        var hashIdx = iri.LastIndexOf('#');
        var slashIdx = iri.LastIndexOf('/');
        var cut = Math.Max(hashIdx, slashIdx);
        return cut < 0 ? iri : iri[(cut + 1)..];
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