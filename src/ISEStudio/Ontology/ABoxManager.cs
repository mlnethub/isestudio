using ISEStudio.Application.Ontology;

namespace ISEStudio.Ontology;

/// <summary>
/// ABox (instance) layer. Mirrors the Python <c>backend/app/ontology/abox.py</c>.
/// Each knowledge system keeps its instances in a separate named graph
/// (<see cref="KsContext.ABoxGraph"/>) so the much-larger instance dataset
/// scales independently of the TBox schema.
/// </summary>
/// <remarks>
/// <para>Mutation methods are synchronous wrappers around the PostgreSQL
/// statement repository; callers that need atomicity should perform the
/// operation within the surrounding database transaction.</para>
/// <para>IRIs are minted from <see cref="KsContext.BaseIri"/> with a uuid4
/// suffix; the caller-supplied "individual IRI" argument is treated as a
/// label / display hint and is never echoed back as the IRI, matching the
/// Python <c>mint_iri</c> contract.</para>
/// <para>This class is statement-shaped end to end. All statement
/// construction goes through <see cref="RdfStatement"/> and
/// <see cref="RdfTerm"/>; the persistence boundary
/// (<c>PostgresABoxGraphStore</c>) consumes the same types.
/// </para>
/// </remarks>
public sealed class ABoxManager
{
    private readonly IABoxGraphStore _store;

    public ABoxManager(IRdfStatementRepository statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        _store = new PostgresABoxGraphStore(statements);
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
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(individualIri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);
        ArgumentNullException.ThrowIfNull(label);

        var iri = MintIri(ks.BaseIri);

        if (_store is null)
        {
            // No graph store wired (contract-test path) — mint the IRI
            // without persisting so the HTTP envelope still parses.
            return iri;
        }

        var statements = new List<RdfStatement>(3)
        {
            new(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlNamedIndividual), ks.ABoxGraph),
            new(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(classIri), ks.ABoxGraph),
        };
        if (label.Length > 0)
        {
            statements.Add(new RdfStatement(
                new RdfIri(iri), Vocabulary.RdfsLabel,
                new RdfLiteral(label), ks.ABoxGraph));
        }
        _store.AddStatements(ks.ABoxGraph, statements);
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
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(individualIri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        var iri = MintIri(ks.BaseIri);

        if (_store is null)
        {
            return iri;
        }

        _store.AddStatements(ks.ABoxGraph, new[]
        {
            new RdfStatement(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlNamedIndividual), ks.ABoxGraph),
            new RdfStatement(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(classIri), ks.ABoxGraph),
        });
        return iri;
    }

    /// <summary>Remove every quad whose subject is <paramref name="iri"/> in the ABox graph.</summary>
    public int DeleteIndividual(KsContext ks, string iri)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);

        if (_store is null)
        {
            return 0;
        }

        var outgoing = _store.Match(subjectIri: iri, graphIri: ks.ABoxGraph);
        if (outgoing.Count == 0) return 0;
        _store.RemoveStatements(ks.ABoxGraph, outgoing);
        return outgoing.Count;
    }

    /// <summary>Add <c>iri rdf:type classIri</c> to the ABox graph.</summary>
    public void AddType(KsContext ks, string iri, string classIri)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        if (_store is null) return;

        _store.AddStatements(ks.ABoxGraph, new[]
        {
            new RdfStatement(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(classIri), ks.ABoxGraph),
        });
    }

    /// <summary>Remove the <c>iri rdf:type classIri</c> triple from the ABox graph.</summary>
    public void RemoveType(KsContext ks, string iri, string classIri)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentException.ThrowIfNullOrEmpty(classIri);

        if (_store is null) return;

        var existing = _store.Match(
            subjectIri: iri,
            predicateIri: Vocabulary.RdfType,
            objectIri: classIri,
            graphIri: ks.ABoxGraph);
        if (existing.Count > 0)
        {
            _store.RemoveStatements(ks.ABoxGraph, existing);
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
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (_store is null)
        {
            return false;
        }

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property,
            objectIri: target, graphIri: ks.ABoxGraph);
        if (existing.Count > 0) return false;
        _store.AddStatements(ks.ABoxGraph, new[]
        {
            new RdfStatement(new RdfIri(subject), property, new RdfIri(target), ks.ABoxGraph),
        });
        return true;
    }

    /// <summary>Remove an object-property assertion.</summary>
    public void RemoveObjectAssertion(KsContext ks, string subject, string property, string target)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (_store is null) return;

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property,
            objectIri: target, graphIri: ks.ABoxGraph);
        if (existing.Count > 0) _store.RemoveStatements(ks.ABoxGraph, existing);
    }

    /// <summary>
    /// Add a data-property assertion <c>(s p "value"^^dt)</c>. <paramref name="datatype"/>
    /// is optional; when <c>null</c> the literal has no explicit datatype (which
    /// means <c>xsd:string</c> per the RDF spec).
    /// </summary>
    public bool AddDataAssertion(KsContext ks, string subject, string property, string value, string? datatype)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);

        if (_store is null) return false;

        var literal = datatype is null
            ? new RdfLiteral(value)
            : new RdfLiteral(value, Datatype: datatype);

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property, graphIri: ks.ABoxGraph);
        foreach (var s in existing)
        {
            // RdfLiteral is a record — value equality checks Value/Language/Datatype.
            if (s.Object is RdfLiteral l && l == literal) return false;
        }
        _store.AddStatements(ks.ABoxGraph, new[]
        {
            new RdfStatement(new RdfIri(subject), property, literal, ks.ABoxGraph),
        });
        return true;
    }

    /// <summary>Remove a data-property assertion.</summary>
    public void RemoveDataAssertion(KsContext ks, string subject, string property, string value, string? datatype)
    {
        Bind(ks);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);

        if (_store is null) return;

        var literal = datatype is null
            ? new RdfLiteral(value)
            : new RdfLiteral(value, Datatype: datatype);

        var existing = _store.Match(
            subjectIri: subject, predicateIri: property, graphIri: ks.ABoxGraph);
        foreach (var s in existing)
        {
            if (s.Object is RdfLiteral l && l == literal)
            {
                _store.RemoveStatements(ks.ABoxGraph, new[] { s });
                return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------

    /// <summary>Every triple in the ABox graph.</summary>
    public IReadOnlyList<RdfStatement> All(KsContext ks) =>
        _store is null
            ? Array.Empty<RdfStatement>()
            : BindAndMatch(ks);

    /// <summary>
    /// A flat <c>iri -&gt; label</c> map for every individual in the ABox
    /// graph, built from a single scan.
    /// </summary>
    public IReadOnlyDictionary<string, string> LabelIndex(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var out_ = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in All(ks))
        {
            if (s.Subject is RdfIri n
                && s.PredicateIri == Vocabulary.RdfsLabel
                && s.Object is RdfLiteral l)
            {
                out_[n.Value] = l.Value;
            }
        }
        return out_;
    }

    /// <summary>Whether any triple exists whose subject is <paramref name="iri"/>.</summary>
    public bool Exists(KsContext ks, string iri) =>
        (_store is not null && BindAndMatch(ks, subjectIri: iri).Count > 0);

    /// <summary>
    /// Returns every individual IRI in the ABox graph — defined as every
    /// subject that has at least one <c>rdf:type</c> triple.
    /// </summary>
    public IReadOnlyList<string> ListIndividuals(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in All(ks))
        {
            if (s.Subject is RdfIri n
                && s.PredicateIri == Vocabulary.RdfType)
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
        foreach (var s in All(ks))
        {
            if (s.Subject is RdfIri
                && s.PredicateIri == Vocabulary.RdfType
                && s.Object is RdfIri cls
                && cls.Value != Vocabulary.OwlNamedIndividual)
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
        foreach (var s in All(ks))
        {
            if (s.Subject is not RdfIri subj) continue;
            if (s.PredicateIri == Vocabulary.RdfType
                && s.Object is RdfIri cls)
            {
                if (!classBySubject.TryGetValue(subj.Value, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    classBySubject[subj.Value] = set;
                }
                set.Add(cls.Value);
            }
            else if (s.PredicateIri == Vocabulary.RdfsLabel
                && s.Object is RdfLiteral lit)
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
            // Mirror Python's `[{"iri": t, "label": class_labels.get(t, t)} for t in sorted(...)]`
            // so the wire shape matches the detail endpoint's IndividualOut.Types
            // and the frontend InstancesPanel can render the type chips without
            // a second round-trip to /abox/classes.
            var types = classBySubject[iri]
                .Where(t => t != Vocabulary.OwlNamedIndividual)
                .OrderBy(t => t, StringComparer.Ordinal)
                .Select(t => new LabeledIri(t,
                    classLabels.TryGetValue(t, out var tl) ? tl : LocalIri(t)))
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
        foreach (var s in All(ks))
        {
            if (s.Subject is not RdfIri subj) continue;
            if (s.PredicateIri == Vocabulary.RdfType
                && s.Object is RdfIri cls)
            {
                if (!classBySubject.TryGetValue(subj.Value, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    classBySubject[subj.Value] = set;
                }
                set.Add(cls.Value);
            }
            else if (s.PredicateIri == Vocabulary.RdfsLabel
                && s.Object is RdfLiteral lit)
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
        Bind(ks);
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

        foreach (var s in outgoing)
        {
            if (s.PredicateIri == Vocabulary.RdfType
                && s.Object is RdfIri cls)
            {
                var clsIri = cls.Value;
                if (clsIri == Vocabulary.OwlNamedIndividual) continue;
                types.Add(new LabeledIri(clsIri,
                    classLabels.TryGetValue(clsIri, out var l) ? l : LocalIri(clsIri)));
            }
            else if (s.PredicateIri == Vocabulary.RdfsLabel
                && s.Object is RdfLiteral labelLit)
            {
                label = labelLit.Value;
            }
            else if (s.Object is RdfIri target)
            {
                var propIri = s.PredicateIri;
                objectAssertions.Add(new ObjectAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Target: target.Value,
                    TargetLabel: LocalIri(target.Value),
                    Sources: Array.Empty<string>()));
            }
            else if (s.Object is RdfLiteral literal)
            {
                var propIri = s.PredicateIri;
                dataAssertions.Add(new DataAssertionOut(
                    Prop: propIri,
                    PropLabel: propLabels.TryGetValue(propIri, out var l) ? l : LocalIri(propIri),
                    Value: literal.Value,
                    Datatype: literal.Datatype,
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

    private void Bind(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        _store?.Bind(ks.KnowledgeSystemId, ks.ABoxGraph);
    }

    private List<RdfStatement> BindAndMatch(
        KsContext ks,
        string? subjectIri = null,
        string? predicateIri = null,
        string? objectIri = null)
    {
        Bind(ks);
        return _store!.Match(subjectIri, predicateIri, objectIri, ks.ABoxGraph);
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

    private interface IABoxGraphStore
    {
        void Bind(Guid knowledgeSystemId, string graphIri);
        List<RdfStatement> Match(string? subjectIri = null, string? predicateIri = null,
            string? objectIri = null, string? graphIri = null);
        void AddStatements(string graphIri, IEnumerable<RdfStatement> statements);
        void RemoveStatements(string graphIri, IEnumerable<RdfStatement> statements);
    }

    private sealed class PostgresABoxGraphStore : IABoxGraphStore
    {
        private readonly IRdfStatementRepository _statements;
        private Guid _knowledgeSystemId;
        private string _graphIri = string.Empty;

        public PostgresABoxGraphStore(IRdfStatementRepository statements) => _statements = statements;

        public void Bind(Guid knowledgeSystemId, string graphIri)
        {
            _knowledgeSystemId = knowledgeSystemId;
            _graphIri = graphIri;
        }

        public List<RdfStatement> Match(string? subjectIri = null, string? predicateIri = null,
            string? objectIri = null, string? graphIri = null)
        {
            var selectedGraph = graphIri ?? _graphIri;
            return _statements.ListAsync(_knowledgeSystemId, "ABox").GetAwaiter().GetResult()
                .Where(s => s.GraphIri == selectedGraph)
                .Where(s => subjectIri is null || s.Subject is RdfIri iri && iri.Value == subjectIri)
                .Where(s => predicateIri is null || s.PredicateIri == predicateIri)
                .Where(s => objectIri is null || s.Object is RdfIri iri && iri.Value == objectIri)
                .ToList();
        }

        public void AddStatements(string graphIri, IEnumerable<RdfStatement> statements)
        {
            var existing = Match(graphIri: graphIri);
            var merged = existing.Concat(statements).Distinct().ToList();
            _statements.ReplaceLayerAsync(_knowledgeSystemId, "ABox", merged)
                .GetAwaiter().GetResult();
        }

        public void RemoveStatements(string graphIri, IEnumerable<RdfStatement> statements)
        {
            var remove = statements.ToHashSet();
            var remaining = Match(graphIri: graphIri)
                .Where(s => !remove.Contains(s))
                .ToList();
            _statements.ReplaceLayerAsync(_knowledgeSystemId, "ABox", remaining)
                .GetAwaiter().GetResult();
        }
    }
}