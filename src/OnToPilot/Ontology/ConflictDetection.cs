using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

/// <summary>
/// Structural ontology conflict / contradiction detection. .NET port of
/// <c>backend/app/ontology/conflicts.py</c>.
///
/// <para>Detects conflict types that are pure graph-shape rules and need no
/// LLM or embedding service:
/// <list type="bullet">
///   <item><description><c>cycle</c> &mdash; subclass cycle (A &sub; &hellip; &sub; A).</description></item>
///   <item><description><c>disjoint_subclass</c> &mdash; A &sub; B while A &perp; B (A unsatisfiable).</description></item>
///   <item><description><c>disjoint_common</c> &mdash; X &sub; A and X &sub; B while A &perp; B.</description></item>
///   <item><description><c>domain_multi</c> / <c>range_multi</c> &mdash; a property has conflicting rdfs:domain / rdfs:range values after collapsing subclass-subsumed values.</description></item>
///   <item><description><c>equiv_disjoint</c> &mdash; A &equiv; B and A &perp; B (direct contradiction).</description></item>
///   <item><description><c>predicate_specialization</c> &mdash; object properties sharing a verb stem whose remainder matches the range noun (e.g. 拥有井 / 拥有计量站 &rarr; 拥有).</description></item>
/// </list></para>
///
/// <para>The semantic duplicate-class pass (embedding cosine + LLM judge) is
/// intentionally deferred &mdash; the project already routes the necessary
/// <c>IEmbeddingGenerator&lt;string, Embedding&gt;</c> through
/// <c>EmbeddingGeneratorFactory</c>, so this file is the only seam that
/// needs to learn about a future <c>ILlmJudge</c> when the prompt-config
/// service lands.</para>
///
/// <para>Each detected conflict carries a stable <see cref="ConflictDetection.DetectedConflict.Signature"/>
/// so the dispatcher can deduplicate re-detected issues, and a
/// <see cref="ConflictDetection.DetectedConflict.Resolutions"/> list whose
/// <c>op</c> dictionaries are exactly the shape
/// <see cref="OntologyEditor.ApplyEditAsync(string, string, IReadOnlyDictionary{string, object?}, CancellationToken)"/>
/// expects.</para>
/// </summary>
public static class ConflictDetection
{
    /// <summary>Threshold above which a <c>SequenceMatcher</c> ratio flags two class labels as a candidate duplicate.</summary>
    public const double DuplicateThreshold = 0.86;

    /// <summary>One detected conflict. Wire-shape mirrors the Python <c>DetectedConflict</c> dataclass.</summary>
    /// <param name="Signature">Stable dedup key (see Python <c>sync_conflicts</c>).</param>
    /// <param name="Ctype">Conflict type &mdash; one of <c>cycle</c>, <c>disjoint_subclass</c>, <c>disjoint_common</c>, <c>domain_multi</c>, <c>range_multi</c>, <c>equiv_disjoint</c>, <c>duplicate</c>, <c>predicate_specialization</c>.</param>
    /// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
    /// <param name="Title">Short headline.</param>
    /// <param name="Detail">Long-form description for the UI.</param>
    /// <param name="Entities">Affected IRIs (each with its label) for evidence linking.</param>
    /// <param name="Resolutions">Suggested editor ops; each <c>op</c> is consumable by <see cref="OntologyEditor.ApplyEditAsync"/>.</param>
    public sealed record DetectedConflict(
        string Signature,
        string Ctype,
        string Severity,
        string Title,
        string Detail,
        IReadOnlyList<EntityRef> Entities,
        IReadOnlyList<Resolution> Resolutions);

    /// <summary>An IRI plus its human label (with unions expanded).</summary>
    public sealed record EntityRef(string Iri, string Label);

    /// <summary>One suggested fix. The <c>op</c> is an <see cref="OntologyEditor.ApplyEditAsync"/> payload.</summary>
    public sealed record Resolution(string Id, string Label, IReadOnlyDictionary<string, object?> Op);

    /// <summary>
    /// Run all structural detectors against <paramref name="graphIri"/>. The
    /// caller is responsible for the per-KS database transaction and the
    /// upsert/auto-clear reconciliation in <c>sync_conflicts</c> (see
    /// <c>ConflictService.DetectAsync</c>).
    /// </summary>
    /// <param name="store">The TBox graph store.</param>
    /// <param name="graphIri">Named graph carrying the TBox quads.</param>
    /// <param name="semantic">Reserved for the deferred duplicate pass; ignored today.</param>
    public static IReadOnlyList<DetectedConflict> Detect(
        StoreWrapper store,
        string graphIri,
        bool semantic = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(graphIri);

        var model = ReadGraph(store, graphIri);
        var found = new List<DetectedConflict>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Push(DetectedConflict c)
        {
            if (seen.Add(c.Signature)) found.Add(c);
        }

        DetectCycles(model, Push);
        var ancestors = Ancestors(model.Subclass);
        var direct = new HashSet<(string, string)>(model.Subclass, GraphPairComparer.Ordinal);
        DetectDisjointSubclass(model, ancestors, direct, Push);
        DetectDisjointCommon(model, ancestors, direct, Push);
        DetectEquivDisjoint(model, Push);
        DetectDomainRangeMulti(model, ancestors, Push);
        DetectPredicateSpecialization(model, Push);
        // Semantic duplicate detection intentionally deferred — see XML doc.

        return found;
    }

    // ----------------------------------------------------------------------
    // Graph read
    // ----------------------------------------------------------------------

    private sealed record GraphModel(
        HashSet<string> Classes,
        Dictionary<string, string> Labels,
        Dictionary<string, PropInfo> Props,
        Dictionary<string, string[]> Unions,
        List<(string Sub, string Sup)> Subclass,
        HashSet<(string A, string B)> Disjoint,
        HashSet<(string A, string B)> Equivalent);

    private sealed class PropInfo
    {
        public string Kind { get; init; } = "object";
        public HashSet<string> Domains { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Ranges { get; } = new(StringComparer.Ordinal);
    }

    private sealed class GraphPairComparer : IEqualityComparer<(string, string)>
    {
        public static readonly GraphPairComparer Ordinal = new();
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.Ordinal)
            && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);
        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(obj.Item1, obj.Item2);
    }

    private static GraphModel ReadGraph(StoreWrapper store, string graphIri)
    {
        var model = new GraphModel(
            Classes: new HashSet<string>(StringComparer.Ordinal),
            Labels: new Dictionary<string, string>(StringComparer.Ordinal),
            Props: new Dictionary<string, PropInfo>(StringComparer.Ordinal),
            Unions: new Dictionary<string, string[]>(StringComparer.Ordinal),
            Subclass: new List<(string, string)>(),
            Disjoint: new HashSet<(string, string)>(GraphPairComparer.Ordinal),
            Equivalent: new HashSet<(string, string)>(GraphPairComparer.Ordinal));

        var unionHead = new Dictionary<string, string>(StringComparer.Ordinal);
        var listFirst = new Dictionary<string, string>(StringComparer.Ordinal);
        var listRest = new Dictionary<string, string>(StringComparer.Ordinal);

        var quads = store.Match(graphIri: graphIri);
        foreach (var q in quads)
        {
            var si = TermIri(q.Subject);
            var pi = q.Predicate.Value;
            var oi = TermIri(q.Object);

            if (pi == Vocabulary.RdfType.Value)
            {
                if (oi == Vocabulary.OwlClass.Value && q.Subject is OntoNamedNode)
                {
                    model.Classes.Add(si);
                }
                else if (oi == Vocabulary.OwlObjectProperty.Value)
                {
                    if (!model.Props.TryGetValue(si, out var pinfo))
                    {
                        pinfo = new PropInfo { Kind = "object" };
                        model.Props[si] = pinfo;
                    }
                }
                else if (oi == Vocabulary.OwlDatatypeProperty.Value)
                {
                    if (!model.Props.TryGetValue(si, out var pinfo))
                    {
                        pinfo = new PropInfo { Kind = "data" };
                        model.Props[si] = pinfo;
                    }
                }
            }
            else if (pi == Vocabulary.RdfsLabel.Value && q.Object is OntoLiteral lbl)
            {
                model.Labels[si] = lbl.Value;
            }
            else if (pi == Vocabulary.RdfsSubClassOf.Value)
            {
                model.Subclass.Add((si, oi));
            }
            else if (pi == Vocabulary.RdfsDomain.Value)
            {
                if (!model.Props.TryGetValue(si, out var pinfo))
                {
                    pinfo = new PropInfo { Kind = "object" };
                    model.Props[si] = pinfo;
                }
                pinfo.Domains.Add(oi);
            }
            else if (pi == Vocabulary.RdfsRange.Value)
            {
                if (!model.Props.TryGetValue(si, out var pinfo))
                {
                    pinfo = new PropInfo { Kind = "object" };
                    model.Props[si] = pinfo;
                }
                pinfo.Ranges.Add(oi);
            }
            else if (pi == Vocabulary.OwlDisjointWith.Value)
            {
                model.Disjoint.Add((si, oi));
            }
            else if (pi == Vocabulary.OwlEquivalentClass.Value)
            {
                model.Equivalent.Add((si, oi));
            }
            else if (pi == Vocabulary.OwlUnionOf.Value)
            {
                unionHead[si] = oi;
            }
            else if (pi == Vocabulary.RdfFirst.Value)
            {
                listFirst[si] = oi;
            }
            else if (pi == Vocabulary.RdfRest.Value)
            {
                listRest[si] = oi;
            }
        }

        // Expand every owl:unionOf head into its rdf:List members.
        foreach (var (head, listHead) in unionHead)
        {
            var members = new List<string>();
            var cur = listHead;
            int guard = 0;
            while (!string.IsNullOrEmpty(cur)
                && cur != Vocabulary.RdfNil.Value
                && guard < 1000)
            {
                if (listFirst.TryGetValue(cur, out var first))
                {
                    members.Add(first);
                }
                listRest.TryGetValue(cur, out cur);
                guard++;
            }
            if (members.Count > 0)
            {
                model.Unions[head] = members.ToArray();
            }
        }

        return model;
    }

    private static string TermIri(object term) => term switch
    {
        OntoNamedNode n => n.Value,
        OntoBlankNode b => b.Value,
        OntoLiteral l => l.Value,
        _ => term.ToString() ?? "",
    };

    private static string LabelOf(GraphModel m, string iri)
    {
        if (m.Unions.TryGetValue(iri, out var members))
        {
            return string.Join(" ∪ ", members.Select(m2 => LabelOf(m, m2)));
        }
        if (m.Labels.TryGetValue(iri, out var lbl))
        {
            return lbl;
        }
        // Local-of-IRI fallback (mirrors Python _local).
        var hash = iri.LastIndexOf('#');
        if (hash >= 0 && hash < iri.Length - 1)
        {
            return iri[(hash + 1)..];
        }
        var slash = iri.LastIndexOf('/');
        if (slash >= 0 && slash < iri.Length - 1)
        {
            return iri[(slash + 1)..];
        }
        return iri;
    }

    private static EntityRef Ent(GraphModel m, string iri) =>
        new(iri, LabelOf(m, iri));

    private static string[] ConcreteValues(GraphModel m, IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var v in values)
        {
            var members = m.Unions.TryGetValue(v, out var arr)
                ? arr
                : new[] { v };
            foreach (var mem in members)
            {
                if (seen.Add(mem)) output.Add(mem);
            }
        }
        return output.ToArray();
    }

    // ----------------------------------------------------------------------
    // Graph algorithms
    // ----------------------------------------------------------------------

    private static Dictionary<string, HashSet<string>> Ancestors(List<(string Sub, string Sup)> subclass)
    {
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (sub, sup) in subclass)
        {
            if (!adj.TryGetValue(sub, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                adj[sub] = set;
            }
            set.Add(sup);
        }

        var cache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        HashSet<string> Walk(string x)
        {
            if (cache.TryGetValue(x, out var hit))
            {
                return hit;
            }
            var acc = new HashSet<string>(StringComparer.Ordinal);
            cache[x] = acc; // cycle guard — keep the empty set in cache
            if (adj.TryGetValue(x, out var sups))
            {
                foreach (var sup in sups)
                {
                    acc.Add(sup);
                    foreach (var anc in Walk(sup))
                    {
                        acc.Add(anc);
                    }
                }
            }
            return acc;
        }

        foreach (var node in adj.Keys.ToList())
        {
            Walk(node);
        }
        return cache;
    }

    private static List<List<string>> StronglyConnectedComponents(
        IReadOnlyCollection<string> nodes,
        IReadOnlyList<(string Sub, string Sup)> subclass)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sub, sup) in subclass)
        {
            if (!adj.TryGetValue(sub, out var list))
            {
                list = new List<string>();
                adj[sub] = list;
            }
            list.Add(sup);
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var counter = 0;
        var output = new List<List<string>>();

        void StrongConnect(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            if (adj.TryGetValue(v, out var ws))
            {
                foreach (var w in ws)
                {
                    if (!index.ContainsKey(w))
                    {
                        StrongConnect(w);
                        low[v] = Math.Min(low[v], low[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                }
            }
            if (low[v] == index[v])
            {
                var comp = new List<string>();
                while (true)
                {
                    var w = stack.Pop();
                    onStack.Remove(w);
                    comp.Add(w);
                    if (w == v) break;
                }
                output.Add(comp);
            }
        }

        foreach (var n in nodes)
        {
            if (!index.ContainsKey(n))
            {
                StrongConnect(n);
            }
        }
        return output;
    }

    // ----------------------------------------------------------------------
    // Detectors
    // ----------------------------------------------------------------------

    private static void DetectCycles(GraphModel m, Action<DetectedConflict> push)
    {
        var edgeSet = new HashSet<(string, string)>(m.Subclass, GraphPairComparer.Ordinal);
        foreach (var comp in StronglyConnectedComponents(m.Classes, m.Subclass))
        {
            var isCycle = comp.Count > 1 || edgeSet.Contains((comp[0], comp[0]));
            if (!isCycle) continue;

            var compSet = new HashSet<string>(comp, StringComparer.Ordinal);
            var intra = m.Subclass.Where(p => compSet.Contains(p.Sub) && compSet.Contains(p.Sup)).ToList();
            var sig = "cycle|" + string.Join("|", comp.OrderBy(x => x, StringComparer.Ordinal));
            var resolutions = intra
                .Select(p => new Resolution(
                    Id: $"rm-{LocalOf(p.Sub)}-{LocalOf(p.Sup)}",
                    Label: $"Delete subclass relation: {LabelOf(m, p.Sub)} ⊑ {LabelOf(m, p.Sup)}",
                    Op: new Dictionary<string, object?>
                    {
                        ["op"] = "delete_axiom",
                        ["type"] = "subclass",
                        ["sub"] = p.Sub,
                        ["super"] = p.Sup,
                    }))
                .ToList<Resolution>();
            push(new DetectedConflict(
                Signature: sig,
                Ctype: "cycle",
                Severity: "error",
                Title: "Subclass cycle",
                Detail: "These classes form a subclass cycle (each is a subclass of another); "
                    + "at least one subclass relation must be removed: "
                    + string.Join(", ", comp.Select(x => LabelOf(m, x))),
                Entities: comp.Select(x => Ent(m, x)).ToList(),
                Resolutions: resolutions));
        }
    }

    private static void DetectDisjointSubclass(
        GraphModel m,
        Dictionary<string, HashSet<string>> ancestors,
        HashSet<(string, string)> direct,
        Action<DetectedConflict> push)
    {
        foreach (var pair in m.Disjoint)
        {
            var (a, b) = pair;
            foreach (var (x, y) in new[] { (a, b), (b, a) })
            {
                if (ancestors.TryGetValue(x, out var ax) && ax.Contains(y))
                {
                    var sig = "disjoint_subclass|" + string.Join("|", new[] { a, b }.OrderBy(s => s, StringComparer.Ordinal));
                    var resolutions = new List<Resolution>
                    {
                        new(Id: "rm-disjoint",
                            Label: $"Delete disjoint declaration: {LabelOf(m, a)} ⟂ {LabelOf(m, b)}",
                            Op: new Dictionary<string, object?>
                            {
                                ["op"] = "delete_axiom",
                                ["type"] = "disjoint",
                                ["a"] = a,
                                ["b"] = b,
                            }),
                    };
                    if (direct.Contains((x, y)))
                    {
                        resolutions.Add(new Resolution(
                            Id: "rm-subclass",
                            Label: $"Delete subclass relation: {LabelOf(m, x)} ⊑ {LabelOf(m, y)}",
                            Op: new Dictionary<string, object?>
                            {
                                ["op"] = "delete_axiom",
                                ["type"] = "subclass",
                                ["sub"] = x,
                                ["super"] = y,
                            }));
                    }
                    push(new DetectedConflict(
                        Signature: sig,
                        Ctype: "disjoint_subclass",
                        Severity: "error",
                        Title: "Disjointness conflict (subclass)",
                        Detail: $"{LabelOf(m, x)} is a (transitive) subclass of {LabelOf(m, y)}, "
                            + $"yet the two are declared disjoint, making {LabelOf(m, x)} unsatisfiable.",
                        Entities: new[] { Ent(m, a), Ent(m, b) },
                        Resolutions: resolutions));
                    break;
                }
            }
        }
    }

    private static void DetectDisjointCommon(
        GraphModel m,
        Dictionary<string, HashSet<string>> ancestors,
        HashSet<(string, string)> direct,
        Action<DetectedConflict> push)
    {
        foreach (var x in m.Classes)
        {
            if (!ancestors.TryGetValue(x, out var ax)) continue;
            foreach (var pair in m.Disjoint)
            {
                var (a, b) = pair;
                if (x == a || x == b) continue;
                if (!ax.Contains(a) || !ax.Contains(b)) continue;

                var sig = $"disjoint_common|{x}|" + string.Join("|", new[] { a, b }.OrderBy(s => s, StringComparer.Ordinal));
                var resolutions = new List<Resolution>
                {
                    new(Id: "rm-disjoint",
                        Label: $"Delete disjoint declaration: {LabelOf(m, a)} ⟂ {LabelOf(m, b)}",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "delete_axiom",
                            ["type"] = "disjoint",
                            ["a"] = a,
                            ["b"] = b,
                        }),
                };
                foreach (var tgt in new[] { a, b })
                {
                    if (direct.Contains((x, tgt)))
                    {
                        resolutions.Add(new Resolution(
                            Id: $"rm-sub-{LocalOf(tgt)}",
                            Label: $"Delete subclass relation: {LabelOf(m, x)} ⊑ {LabelOf(m, tgt)}",
                            Op: new Dictionary<string, object?>
                            {
                                ["op"] = "delete_axiom",
                                ["type"] = "subclass",
                                ["sub"] = x,
                                ["super"] = tgt,
                            }));
                    }
                }
                push(new DetectedConflict(
                    Signature: sig,
                    Ctype: "disjoint_common",
                    Severity: "error",
                    Title: "Disjointness conflict (common subclass)",
                    Detail: $"{LabelOf(m, x)} is a subclass of both disjoint classes "
                        + $"{LabelOf(m, a)} and {LabelOf(m, b)}, making {LabelOf(m, x)} unsatisfiable.",
                    Entities: new[] { Ent(m, x), Ent(m, a), Ent(m, b) },
                    Resolutions: resolutions));
            }
        }
    }

    private static void DetectEquivDisjoint(GraphModel m, Action<DetectedConflict> push)
    {
        foreach (var pair in m.Equivalent)
        {
            if (!m.Disjoint.Contains(pair)) continue;
            var (a, b) = pair;
            var sig = "equiv_disjoint|" + string.Join("|", new[] { a, b }.OrderBy(s => s, StringComparer.Ordinal));
            push(new DetectedConflict(
                Signature: sig,
                Ctype: "equiv_disjoint",
                Severity: "error",
                Title: "Equivalent vs. disjoint contradiction",
                Detail: $"{LabelOf(m, a)} and {LabelOf(m, b)} are declared both equivalent and "
                    + "disjoint — a direct contradiction.",
                Entities: new[] { Ent(m, a), Ent(m, b) },
                Resolutions: new[]
                {
                    new Resolution(
                        Id: "rm-equivalent",
                        Label: "Delete equivalent declaration",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "delete_axiom",
                            ["type"] = "equivalent",
                            ["a"] = a,
                            ["b"] = b,
                        }),
                    new Resolution(
                        Id: "rm-disjoint",
                        Label: "Delete disjoint declaration",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "delete_axiom",
                            ["type"] = "disjoint",
                            ["a"] = a,
                            ["b"] = b,
                        }),
                }));
        }
    }

    private static void DetectDomainRangeMulti(
        GraphModel m,
        Dictionary<string, HashSet<string>> ancestors,
        Action<DetectedConflict> push)
    {
        foreach (var (iri, info) in m.Props)
        {
            foreach (var (slot, values) in new[] { ("domain", info.Domains), ("range", info.Ranges) })
            {
                if (values.Count < 2) continue;

                // Drop values subsumed by a more-general one in the set.
                // {A, B} with A⊑B collapses to {B} (B is the union / general
                // class — redundant, not a conflict).
                var vals = values
                    .Where(v => !values.Any(w =>
                        w != v && ancestors.TryGetValue(w, out var anc) && anc.Contains(v)))
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .ToArray();
                if (vals.Length < 2) continue;

                var ctype = slot == "domain" ? "domain_multi" : "range_multi";
                var slotLabel = slot; // already "domain" or "range"
                var concrete = ConcreteValues(m, vals);

                var resolutions = new List<Resolution>();
                foreach (var v in vals)
                {
                    var op = m.Unions.ContainsKey(v)
                        ? new Dictionary<string, object?>
                        {
                            ["op"] = "update_property",
                            ["iri"] = iri,
                            ["clear_" + slot] = true,
                        }
                        : new Dictionary<string, object?>
                        {
                            ["op"] = "update_property",
                            ["iri"] = iri,
                            [slot] = v,
                        };
                    resolutions.Add(new Resolution(
                        Id: $"keep-{LocalOf(v)}",
                        Label: $"Keep only {slotLabel} = {LabelOf(m, v)}",
                        Op: op));
                }

                // For class-valued slots, offer a common-superclass collapse.
                if (concrete.All(v => !v.StartsWith(Vocabulary.Xsd, StringComparison.Ordinal)))
                {
                    HashSet<string>? common = null;
                    foreach (var v in concrete)
                    {
                        if (!ancestors.TryGetValue(v, out var anc)) continue;
                        common = common is null
                            ? new HashSet<string>(anc, StringComparer.Ordinal)
                            : new HashSet<string>(common.Intersect(anc, StringComparer.Ordinal), StringComparer.Ordinal);
                    }
                    if (common is { Count: > 0 })
                    {
                        var sSuper = common.OrderByDescending(s => ancestors.TryGetValue(s, out var anc) ? anc.Count : 0).First();
                        resolutions.Add(new Resolution(
                            Id: $"super-{LocalOf(sSuper)}",
                            Label: $"Use common superclass {LabelOf(m, sSuper)}",
                            Op: new Dictionary<string, object?>
                            {
                                ["op"] = "update_property",
                                ["iri"] = iri,
                                [slot] = sSuper,
                            }));
                    }
                    // The union fix would require set_property_union which
                    // OntologyEditor doesn't ship yet — surface as a hint
                    // resolution that's flagged not-yet-supported rather
                    // than silently failing. UI shows it greyed out.
                    resolutions.Add(new Resolution(
                        Id: "union",
                        Label: $"Use union {slotLabel} ({string.Join(" ∪ ", concrete.Select(c => LabelOf(m, c)))})",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "noop",
                            ["reason"] = "set_property_union not yet implemented in .NET editor",
                        }));
                }

                push(new DetectedConflict(
                    Signature: $"{ctype}|{iri}",
                    Ctype: ctype,
                    Severity: "warning",
                    Title: $"{char.ToUpperInvariant(slotLabel[0])}{slotLabel[1..]} conflict",
                    Detail: $"Property \"{LabelOf(m, iri)}\" has multiple {slotLabel}s: "
                        + string.Join(", ", vals.Select(v => LabelOf(m, v)))
                        + $". Multiple {slotLabel}s mean their intersection (all must hold), "
                        + "which is usually not intended — use a union, a common superclass, or keep just one.",
                    Entities: new[] { Ent(m, iri) }.Concat(vals.Select(v => Ent(m, v))).ToList(),
                    Resolutions: resolutions));
            }
        }
    }

    private static void DetectPredicateSpecialization(GraphModel m, Action<DetectedConflict> push)
    {
        var objProps = m.Props
            .Where(kv => kv.Value.Kind == "object")
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // For each object property, find the longest range label that's a
        // complete suffix (or, for CJK, character suffix) of the property
        // label. That suffix is the candidate "baked-in object noun".
        var stemOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (iri, info) in objProps)
        {
            var propLabel = LabelOf(m, iri);
            string? bestStem = null;
            int bestLen = 0;
            foreach (var r in info.Ranges)
            {
                var rangeLabel = LabelOf(m, r);
                var stem = SpecializationStem(propLabel, rangeLabel);
                if (stem is null) continue;
                var rangeNormLen = Vocabulary.NormLabel(rangeLabel).Length;
                if (rangeNormLen > bestLen)
                {
                    bestStem = stem;
                    bestLen = rangeNormLen;
                }
            }
            if (bestStem is not null)
            {
                stemOf[iri] = bestStem;
            }
        }

        var families = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (iri, stem) in stemOf)
        {
            if (!families.TryGetValue(stem, out var list))
            {
                list = new List<string>();
                families[stem] = list;
            }
            list.Add(iri);
        }

        foreach (var (stem, members) in families)
        {
            if (members.Count < 2) continue;
            members.Sort(StringComparer.Ordinal);
            var labels = members.Select(i => LabelOf(m, i)).ToArray();
            // Look for an existing property whose label equals the stem
            // exactly — that's the natural merge target.
            var targetIri = objProps.Keys.FirstOrDefault(i => LabelOf(m, i) == stem);

            push(new DetectedConflict(
                Signature: "predspec|" + stem + "|" + string.Join("|", members),
                Ctype: "predicate_specialization",
                Severity: "warning",
                Title: "Over-specialized relations",
                Detail: $"{string.Join(", ", labels.Select(l => "\"" + l + "\""))} look like the general "
                    + $"relation \"{stem}\" specialized by object type — the object's class already "
                    + $"carries that. Merge into \"{stem}\", make them sub-properties, or dismiss.",
                Entities: members.Select(i => Ent(m, i)).ToList(),
                Resolutions: new Resolution[]
                {
                    new(
                        Id: "merge",
                        Label: $"Merge into \"{stem}\"",
                        // merge_properties not yet wired in OntologyEditor —
                        // surface as a noop so the UI can show the option
                        // greyed out instead of a 500.
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "noop",
                            ["reason"] = "merge_properties not yet implemented in .NET editor",
                        }),
                    new(
                        Id: "subprop",
                        Label: $"Sub-properties of \"{stem}\"",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "noop",
                            ["reason"] = "subordinate_properties not yet implemented in .NET editor",
                        }),
                }));
        }
    }

    /// <summary>
    /// Return a meaningful relation stem when the range name is a complete
    /// suffix of the property label. Mirrors <c>_specialization_stem</c> in
    /// the Python detector &mdash; see its docstring for the CJK fallback
    /// (which we keep simple: a character-level suffix match).
    /// </summary>
    private static string? SpecializationStem(string propertyLabel, string rangeLabel)
    {
        var propertyNorm = Vocabulary.NormLabel(propertyLabel);
        var rangeNorm = Vocabulary.NormLabel(rangeLabel);
        if (string.IsNullOrEmpty(propertyNorm) || string.IsNullOrEmpty(rangeNorm)) return null;

        var latinLike = propertyNorm.Any(char.IsAsciiLetter) || rangeNorm.Any(char.IsAsciiLetter);
        if (latinLike)
        {
            var propertyTokens = propertyNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rangeTokens = rangeNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (propertyTokens.Length <= rangeTokens.Length) return null;
            // Verify propertyTokens ends with rangeTokens (same logic as the
            // Python slice comparison `property_tokens[-len(range_tokens):] != range_tokens`).
            for (int k = 0; k < rangeTokens.Length; k++)
            {
                if (!string.Equals(propertyTokens[^(rangeTokens.Length - k)], rangeTokens[k], StringComparison.Ordinal))
                {
                    return null;
                }
            }
            var stem = string.Join(' ', propertyTokens[..^rangeTokens.Length]).Trim();
            if (string.IsNullOrEmpty(stem)) return null;
            // Uninformative Latin stems (e.g. "has") never form a meaningful relation.
            if (UninformativeLatinStems.Contains(stem)) return null;
            return stem;
        }

        // CJK / non-ASCII path: shared trailing characters.
        var common = 0;
        while (common < propertyNorm.Length && common < rangeNorm.Length
            && propertyNorm[^(common + 1)] == rangeNorm[^(common + 1)])
        {
            common++;
        }
        if (common != rangeNorm.Length) return null;
        var cjkStem = propertyNorm[..^common].Trim();
        return cjkStem.Length >= 2 ? cjkStem : null;
    }

    private static readonly HashSet<string> UninformativeLatinStems =
        new(StringComparer.OrdinalIgnoreCase) { "be", "is", "are", "was", "were", "has", "have", "had", "with" };

    private static string LocalOf(string iri)
    {
        var hash = iri.LastIndexOf('#');
        if (hash >= 0 && hash < iri.Length - 1) return iri[(hash + 1)..];
        var slash = iri.LastIndexOf('/');
        if (slash >= 0 && slash < iri.Length - 1) return iri[(slash + 1)..];
        return iri;
    }
}
