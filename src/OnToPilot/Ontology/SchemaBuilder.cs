using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoDefaultGraph = Oxigraph.DefaultGraph;

namespace OnToPilot.Ontology;

// ----------------------------------------------------------------------
// Mutation DTOs
// ----------------------------------------------------------------------

/// <summary>
/// A requested class declaration. <see cref="RoleVerified"/> mirrors the
/// Python <c>_role_verified</c> flag: a class label that an independent role
/// critic has confirmed is a reusable type rather than an individual.
/// </summary>
public sealed record ClassMutation(string Label, string? Comment = null, bool RoleVerified = false);

/// <summary>
/// A requested property declaration. <see cref="Kind"/> is <c>"object"</c> or
/// <c>"data"</c> (stringly-typed for API parity with the Python payload).
/// </summary>
public sealed record PropertyMutation(
    string Label,
    string Kind,
    string? Comment = null,
    string? Domain = null,
    string? Range = null);

/// <summary>
/// A requested class-level axiom. <see cref="Type"/> is <c>"subclass"</c>,
/// <c>"disjoint"</c>, or <c>"equivalent"</c>.
/// </summary>
public sealed record AxiomMutation(
    string Type,
    string? Sub = null,
    string? Super = null,
    string? A = null,
    string? B = null);

/// <summary>
/// Aggregate of class / property / axiom mutations to translate into RDF
/// quads. <see cref="SchemaBuilder.BuildMutation"/> consumes one of these and
/// returns the corresponding <c>IReadOnlyList&lt;Quad&gt;</c>.
/// </summary>
public sealed record OntologyMutation(
    IReadOnlyList<ClassMutation> Classes,
    IReadOnlyList<PropertyMutation> ObjectProperties,
    IReadOnlyList<PropertyMutation> DataProperties,
    IReadOnlyList<AxiomMutation> Axioms);

// ----------------------------------------------------------------------
// View DTOs
// ----------------------------------------------------------------------

/// <summary>Curated view of a TBox named graph — what the frontend sees.</summary>
public sealed record OntologyView(
    IReadOnlyList<ClassView> Classes,
    IReadOnlyList<PropertyView> ObjectProperties,
    IReadOnlyList<PropertyView> DataProperties,
    AxiomView Axioms);

public sealed record ClassView(
    string Iri,
    string Local,
    string Label,
    string Comment,
    IReadOnlyList<string> Superclasses);

public sealed record PropertyView(
    string Iri,
    string Local,
    string Label,
    string Comment,
    string? Domain,
    string? DomainLabel,
    string? Range,
    string? RangeLabel,
    IReadOnlyList<string> DomainMembers,
    IReadOnlyList<string> RangeMembers);

public sealed record AxiomView(
    IReadOnlyList<AxiomPair> SubClassOf,
    IReadOnlyList<AxiomPair> DisjointWith,
    IReadOnlyList<AxiomPair> EquivalentClass);

public sealed record AxiomPair(string A, string B);

// ----------------------------------------------------------------------
// BuildMutation / BuildView
// ----------------------------------------------------------------------

/// <summary>
/// .NET port of the Python <c>schema.build_mutation</c> /
/// <c>schema.build_view</c> pair. Translates structured mutation DTOs into
/// RDF quads that the caller applies through <see cref="StoreWrapper"/>, and
/// reads a named graph back into the curated view the frontend consumes.
/// </summary>
public static class SchemaBuilder
{
    /// <summary>
    /// Translate an <see cref="OntologyMutation"/> into quads against
    /// <paramref name="baseIri"/> and emit them into <paramref name="graphIri"/>.
    /// Referenced-but-undeclared classes are auto-declared in this run;
    /// Oxigraph collapses identical quads so re-running across chunks is
    /// idempotent at the triple level.
    /// </summary>
    public static IReadOnlyList<OntoQuad> BuildMutation(
        string baseIri, OntologyMutation mutation, string graphIri)
    {
        ArgumentNullException.ThrowIfNull(baseIri);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentException.ThrowIfNullOrEmpty(graphIri);

        var graph = new OntoNamedNode(graphIri);
        var triples = new List<OntoQuad>();
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var seenProps = new HashSet<string>(StringComparer.Ordinal);
        var labeledRun = new HashSet<string>(StringComparer.Ordinal);

        void AddLabel(OntoNamedNode node, string label)
        {
            if (labeledRun.Add(node.Value))
            {
                triples.Add(new OntoQuad(node, Vocabulary.RdfsLabel, new OntoLiteral(label), graph));
            }
        }

        OntoNamedNode EnsureClass(string label)
        {
            var local = Vocabulary.ClassLocalName(label);
            if (seenClasses.Add(local))
            {
                var node = new OntoNamedNode(baseIri + local);
                triples.Add(new OntoQuad(node, Vocabulary.RdfType, Vocabulary.OwlClass, graph));
                AddLabel(node, label);
            }
            return new OntoNamedNode(baseIri + local);
        }

        OntoNamedNode DeclareProperty(string label, bool isObject)
        {
            var local = Vocabulary.PropertyLocalName(label);
            if (seenProps.Add(local))
            {
                var node = new OntoNamedNode(baseIri + local);
                var ptype = isObject ? Vocabulary.OwlObjectProperty : Vocabulary.OwlDatatypeProperty;
                triples.Add(new OntoQuad(node, Vocabulary.RdfType, ptype, graph));
                AddLabel(node, label);
            }
            return new OntoNamedNode(baseIri + local);
        }

        // Classes (explicit first so their comments/labels take precedence).
        foreach (var c in mutation.Classes ?? Array.Empty<ClassMutation>())
        {
            if (string.IsNullOrWhiteSpace(c.Label)) continue;
            var node = EnsureClass(c.Label);
            if (!string.IsNullOrEmpty(c.Comment))
            {
                triples.Add(new OntoQuad(node, Vocabulary.RdfsComment, new OntoLiteral(c.Comment), graph));
            }
        }

        // Object properties.
        foreach (var p in mutation.ObjectProperties ?? Array.Empty<PropertyMutation>())
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            var node = DeclareProperty(p.Label, isObject: true);
            if (!string.IsNullOrEmpty(p.Comment))
            {
                triples.Add(new OntoQuad(node, Vocabulary.RdfsComment, new OntoLiteral(p.Comment), graph));
            }
            if (!string.IsNullOrWhiteSpace(p.Domain))
            {
                var dnode = EnsureClass(p.Domain);
                triples.Add(new OntoQuad(node, Vocabulary.RdfsDomain, dnode, graph));
            }
            if (!string.IsNullOrWhiteSpace(p.Range))
            {
                var rnode = EnsureClass(p.Range);
                triples.Add(new OntoQuad(node, Vocabulary.RdfsRange, rnode, graph));
            }
        }

        // Data properties.
        foreach (var p in mutation.DataProperties ?? Array.Empty<PropertyMutation>())
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            var node = DeclareProperty(p.Label, isObject: false);
            if (!string.IsNullOrEmpty(p.Comment))
            {
                triples.Add(new OntoQuad(node, Vocabulary.RdfsComment, new OntoLiteral(p.Comment), graph));
            }
            if (!string.IsNullOrWhiteSpace(p.Domain))
            {
                var dnode = EnsureClass(p.Domain);
                triples.Add(new OntoQuad(node, Vocabulary.RdfsDomain, dnode, graph));
            }
            // Range defaults to xsd:string if the caller passes nothing.
            var rangeNode = Vocabulary.DatatypeNode(p.Range);
            triples.Add(new OntoQuad(node, Vocabulary.RdfsRange, rangeNode, graph));
        }

        // Class axioms.
        foreach (var ax in mutation.Axioms ?? Array.Empty<AxiomMutation>())
        {
            switch (ax.Type)
            {
                case "subclass":
                    if (string.IsNullOrWhiteSpace(ax.Sub) || string.IsNullOrWhiteSpace(ax.Super)) break;
                    var subNode = EnsureClass(ax.Sub!);
                    var supNode = EnsureClass(ax.Super!);
                    if (subNode.Value != supNode.Value)
                    {
                        triples.Add(new OntoQuad(subNode, Vocabulary.RdfsSubClassOf, supNode, graph));
                    }
                    break;
                case "disjoint":
                    if (string.IsNullOrWhiteSpace(ax.A) || string.IsNullOrWhiteSpace(ax.B)) break;
                    var aNode = EnsureClass(ax.A!);
                    var bNode = EnsureClass(ax.B!);
                    if (aNode.Value != bNode.Value)
                    {
                        triples.Add(new OntoQuad(aNode, Vocabulary.OwlDisjointWith, bNode, graph));
                    }
                    break;
                case "equivalent":
                    if (string.IsNullOrWhiteSpace(ax.A) || string.IsNullOrWhiteSpace(ax.B)) break;
                    var eaNode = EnsureClass(ax.A!);
                    var ebNode = EnsureClass(ax.B!);
                    if (eaNode.Value != ebNode.Value)
                    {
                        triples.Add(new OntoQuad(eaNode, Vocabulary.OwlEquivalentClass, ebNode, graph));
                    }
                    break;
            }
        }

        return triples;
    }

    // ------------------------------------------------------------------
    // BuildView
    // ------------------------------------------------------------------

    /// <summary>
    /// Read the named graph out of <paramref name="store"/> into a curated
    /// view the frontend consumes. Anonymous owl:unionOf expressions are
    /// expanded; multi-valued domain/range triples are surfaced as
    /// <see cref="PropertyView.DomainMembers"/> / <see cref="PropertyView.RangeMembers"/>.
    /// </summary>
    public static OntologyView BuildView(string graphIri, StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(graphIri);
        ArgumentNullException.ThrowIfNull(store);

        var graph = new OntoNamedNode(graphIri);
        var quads = store.Match(graph: graph);

        var classes = new Dictionary<string, ClassView>(StringComparer.Ordinal);
        var objProps = new Dictionary<string, PropertyView>(StringComparer.Ordinal);
        var dataProps = new Dictionary<string, PropertyView>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var subClassOf = new List<AxiomPair>();
        var disjoint = new List<AxiomPair>();
        var equivalent = new List<AxiomPair>();
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, string>(StringComparer.Ordinal);
        var domainAll = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rangeAll = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var q in quads)
        {
            var siri = TermIri(q.Subject);
            var piri = q.Predicate.Value;
            var oiri = TermIri(q.Object);

            if (piri == Vocabulary.RdfType.Value)
            {
                if (oiri == Vocabulary.OwlClass.Value && q.Subject is OntoNamedNode)
                {
                    if (!classes.ContainsKey(siri))
                    {
                        classes[siri] = new ClassView(
                            Iri: siri, Local: LocalOf(siri), Label: "", Comment: "",
                            Superclasses: new List<string>());
                    }
                }
                else if (oiri == Vocabulary.OwlObjectProperty.Value)
                {
                    if (!objProps.ContainsKey(siri))
                    {
                        objProps[siri] = new PropertyView(
                            Iri: siri, Local: LocalOf(siri), Label: "",
                            Comment: "", Domain: null, DomainLabel: null,
                            Range: null, RangeLabel: null,
                            DomainMembers: new List<string>(),
                            RangeMembers: new List<string>());
                    }
                }
                else if (oiri == Vocabulary.OwlDatatypeProperty.Value)
                {
                    if (!dataProps.ContainsKey(siri))
                    {
                        dataProps[siri] = new PropertyView(
                            Iri: siri, Local: LocalOf(siri), Label: "",
                            Comment: "", Domain: null, DomainLabel: null,
                            Range: null, RangeLabel: null,
                            DomainMembers: new List<string>(),
                            RangeMembers: new List<string>());
                    }
                }
            }
            else if (piri == Vocabulary.RdfsLabel.Value)
            {
                labels[siri] = ((OntoLiteral)q.Object).Value;
            }
            else if (piri == Vocabulary.RdfsComment.Value)
            {
                comments[siri] = ((OntoLiteral)q.Object).Value;
            }
            else if (piri == Vocabulary.RdfsSubClassOf.Value)
            {
                subClassOf.Add(new AxiomPair(siri, oiri));
            }
            else if (piri == Vocabulary.RdfsDomain.Value)
            {
                domains[siri] = oiri;
                if (!domainAll.TryGetValue(siri, out var list))
                {
                    list = new List<string>();
                    domainAll[siri] = list;
                }
                list.Add(oiri);
            }
            else if (piri == Vocabulary.RdfsRange.Value)
            {
                ranges[siri] = oiri;
                if (!rangeAll.TryGetValue(siri, out var list))
                {
                    list = new List<string>();
                    rangeAll[siri] = list;
                }
                list.Add(oiri);
            }
            else if (piri == Vocabulary.OwlDisjointWith.Value)
            {
                disjoint.Add(new AxiomPair(siri, oiri));
            }
            else if (piri == Vocabulary.OwlEquivalentClass.Value)
            {
                equivalent.Add(new AxiomPair(siri, oiri));
            }
        }

        var superMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in subClassOf)
        {
            if (!superMap.TryGetValue(r.A, out var list))
            {
                list = new List<string>();
                superMap[r.A] = list;
            }
            list.Add(r.B);
        }

        string LabelOf(string iri) => labels.TryGetValue(iri, out var l) ? l : LocalOf(iri);

        var classList = classes.Keys
            .OrderBy(LabelOf, StringComparer.Ordinal)
            .Select(iri => new ClassView(
                Iri: iri,
                Local: LocalOf(iri),
                Label: LabelOf(iri),
                Comment: comments.TryGetValue(iri, out var c) ? c : "",
                Superclasses: superMap.TryGetValue(iri, out var sups)
                    ? (IReadOnlyList<string>)sups
                    : Array.Empty<string>()))
            .ToList();

        var objList = objProps.Keys
            .OrderBy(LabelOf, StringComparer.Ordinal)
            .Select(iri => PropEntry(iri, isDatatypeRange: false, labels, comments, domains, ranges, domainAll, rangeAll, LabelOf))
            .ToList();
        var dataList = dataProps.Keys
            .OrderBy(LabelOf, StringComparer.Ordinal)
            .Select(iri => PropEntry(iri, isDatatypeRange: true, labels, comments, domains, ranges, domainAll, rangeAll, LabelOf))
            .ToList();

        return new OntologyView(
            Classes: classList,
            ObjectProperties: objList,
            DataProperties: dataList,
            Axioms: new AxiomView(subClassOf, disjoint, equivalent));
    }

    // The graph the schema writes into. BuildMutation is a pure projection
    // (it returns quads but doesn't write), so the graph is purely a
    // serialization placeholder; callers add the quads to whatever named
    // graph they want.
    // BuildMutation now takes the target graph IRI as a parameter so the
    // emitted quads land in the caller's graph, not a placeholder.

    private static string LocalOf(string iri) =>
        iri.Contains('#') ? iri[(iri.LastIndexOf('#') + 1)..] : iri.TrimEnd('/').Split('/')[^1];

    private static string TermIri(object term) => term switch
    {
        OntoNamedNode n => n.Value,
        OntoBlankNode b => b.Value,
        OntoLiteral l => l.Value,
        _ => term.ToString() ?? "",
    };

    private static PropertyView PropEntry(
        string iri,
        bool isDatatypeRange,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> comments,
        IReadOnlyDictionary<string, string> domains,
        IReadOnlyDictionary<string, string> ranges,
        IReadOnlyDictionary<string, List<string>> domainAll,
        IReadOnlyDictionary<string, List<string>> rangeAll,
        Func<string, string> labelOf)
    {
        string? domainLabel = null;
        if (domains.TryGetValue(iri, out var dval))
        {
            domainLabel = labelOf(dval);
        }
        string? rangeLabel = null;
        if (ranges.TryGetValue(iri, out var rval))
        {
            rangeLabel = rval.StartsWith(Vocabulary.Xsd, StringComparison.Ordinal)
                ? "xsd:" + LocalOf(rval)
                : labelOf(rval);
        }
        return new PropertyView(
            Iri: iri,
            Local: LocalOf(iri),
            Label: labels.TryGetValue(iri, out var l) ? l : LocalOf(iri),
            Comment: comments.TryGetValue(iri, out var c) ? c : "",
            Domain: domains.TryGetValue(iri, out var d) ? d : null,
            DomainLabel: domainLabel,
            Range: ranges.TryGetValue(iri, out var r) ? r : null,
            RangeLabel: rangeLabel,
            DomainMembers: domainAll.TryGetValue(iri, out var dm) ? dm : Array.Empty<string>(),
            RangeMembers: rangeAll.TryGetValue(iri, out var rm) ? rm : Array.Empty<string>());
    }
}