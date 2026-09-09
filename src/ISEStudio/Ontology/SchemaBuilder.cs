namespace ISEStudio.Ontology;

// ----------------------------------------------------------------------
// Mutation DTOs
// ----------------------------------------------------------------------

/// <summary>
/// A requested class declaration. <see cref="RoleVerified"/> mirrors the
/// Python <c>_role_verified</c> flag: a class label that an independent role
/// critic has confirmed is a reusable type rather than an individual.
/// <see cref="Evidence"/> carries the source span the LLM extracted it from
/// (Python <c>extract.py</c> <c>classes[].evidence</c>); it is advisory
/// context for the verify critic, not a decision input — the critic always
/// re-quotes the source on its own.
/// </summary>
public sealed record ClassMutation(
    string Label,
    string? Comment = null,
    bool RoleVerified = false,
    string? Evidence = null);

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
/// <c>"disjoint"</c>, or <c>"equivalent"</c>. <see cref="Evidence"/> is
/// populated only for <c>subclass</c> axioms (Python
/// <c>subclass_of[].evidence</c>); <c>disjoint</c> / <c>equivalent</c>
/// never carry it.
/// </summary>
public sealed record AxiomMutation(
    string Type,
    string? Sub = null,
    string? Super = null,
    string? A = null,
    string? B = null,
    string? Evidence = null);

/// <summary>
/// Aggregate of class / property / axiom mutations to translate into RDF
/// statements. <see cref="SchemaBuilder.BuildMutationStatements"/> consumes
/// one of these and returns the corresponding
/// <c>IReadOnlyList&lt;RdfStatement&gt;</c>.
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
/// RDF statements that the caller applies through the PostgreSQL RDF
/// layer, and reads a named graph back into the curated view the
/// frontend consumes.
/// </summary>
public static class SchemaBuilder
{
    /// <summary>
    /// Translate an <see cref="OntologyMutation"/> into statements against
    /// <paramref name="baseIri"/> and emit them into
    /// <paramref name="graphIri"/>. Referenced-but-undeclared classes are
    /// auto-declared in this run; duplicate statements collapse so
    /// re-running across chunks is idempotent at the triple level.
    /// </summary>
    public static IReadOnlyList<RdfStatement> BuildMutationStatements(
        string baseIri, OntologyMutation mutation, string graphIri)
    {
        ArgumentNullException.ThrowIfNull(baseIri);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentException.ThrowIfNullOrEmpty(graphIri);

        var statements = new List<RdfStatement>();
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var seenProps = new HashSet<string>(StringComparer.Ordinal);
        var labeledRun = new HashSet<string>(StringComparer.Ordinal);

        void AddLabel(RdfIri iri, string label)
        {
            if (labeledRun.Add(iri.Value))
            {
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsLabel, new RdfLiteral(label), graphIri));
            }
        }

        RdfIri EnsureClass(string label)
        {
            var local = Vocabulary.ClassLocalName(label);
            if (seenClasses.Add(local))
            {
                var iri = new RdfIri(baseIri + local);
                statements.Add(new RdfStatement(iri, Vocabulary.RdfType, new RdfIri(Vocabulary.OwlClass), graphIri));
                AddLabel(iri, label);
            }
            return new RdfIri(baseIri + local);
        }

        RdfIri DeclareProperty(string label, bool isObject)
        {
            var local = Vocabulary.PropertyLocalName(label);
            if (seenProps.Add(local))
            {
                var iri = new RdfIri(baseIri + local);
                var ptypeIri = new RdfIri(isObject ? Vocabulary.OwlObjectProperty : Vocabulary.OwlDatatypeProperty);
                statements.Add(new RdfStatement(iri, Vocabulary.RdfType, ptypeIri, graphIri));
                AddLabel(iri, label);
            }
            return new RdfIri(baseIri + local);
        }

        // Classes (explicit first so their comments/labels take precedence).
        foreach (var c in mutation.Classes ?? Array.Empty<ClassMutation>())
        {
            if (string.IsNullOrWhiteSpace(c.Label)) continue;
            var iri = EnsureClass(c.Label);
            if (!string.IsNullOrEmpty(c.Comment))
            {
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsComment, new RdfLiteral(c.Comment), graphIri));
            }
        }

        // Object properties.
        foreach (var p in mutation.ObjectProperties ?? Array.Empty<PropertyMutation>())
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            var iri = DeclareProperty(p.Label, isObject: true);
            if (!string.IsNullOrEmpty(p.Comment))
            {
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsComment, new RdfLiteral(p.Comment), graphIri));
            }
            if (!string.IsNullOrWhiteSpace(p.Domain))
            {
                var diri = EnsureClass(p.Domain);
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsDomain, diri, graphIri));
            }
            if (!string.IsNullOrWhiteSpace(p.Range))
            {
                var riri = EnsureClass(p.Range);
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsRange, riri, graphIri));
            }
        }

        // Data properties.
        foreach (var p in mutation.DataProperties ?? Array.Empty<PropertyMutation>())
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            var iri = DeclareProperty(p.Label, isObject: false);
            if (!string.IsNullOrEmpty(p.Comment))
            {
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsComment, new RdfLiteral(p.Comment), graphIri));
            }
            if (!string.IsNullOrWhiteSpace(p.Domain))
            {
                var diri = EnsureClass(p.Domain);
                statements.Add(new RdfStatement(iri, Vocabulary.RdfsDomain, diri, graphIri));
            }
            // Range defaults to xsd:string if the caller passes nothing.
            var rangeIri = new RdfIri(Vocabulary.DatatypeNode(p.Range));
            statements.Add(new RdfStatement(iri, Vocabulary.RdfsRange, rangeIri, graphIri));
        }

        // Class axioms.
        foreach (var ax in mutation.Axioms ?? Array.Empty<AxiomMutation>())
        {
            switch (ax.Type)
            {
                case "subclass":
                    if (string.IsNullOrWhiteSpace(ax.Sub) || string.IsNullOrWhiteSpace(ax.Super)) break;
                    var subIri = EnsureClass(ax.Sub!);
                    var supIri = EnsureClass(ax.Super!);
                    if (subIri.Value != supIri.Value)
                    {
                        statements.Add(new RdfStatement(subIri, Vocabulary.RdfsSubClassOf, supIri, graphIri));
                    }
                    break;
                case "disjoint":
                    if (string.IsNullOrWhiteSpace(ax.A) || string.IsNullOrWhiteSpace(ax.B)) break;
                    var aIri = EnsureClass(ax.A!);
                    var bIri = EnsureClass(ax.B!);
                    if (aIri.Value != bIri.Value)
                    {
                        statements.Add(new RdfStatement(aIri, Vocabulary.OwlDisjointWith, bIri, graphIri));
                    }
                    break;
                case "equivalent":
                    if (string.IsNullOrWhiteSpace(ax.A) || string.IsNullOrWhiteSpace(ax.B)) break;
                    var eaIri = EnsureClass(ax.A!);
                    var ebIri = EnsureClass(ax.B!);
                    if (eaIri.Value != ebIri.Value)
                    {
                        statements.Add(new RdfStatement(eaIri, Vocabulary.OwlEquivalentClass, ebIri, graphIri));
                    }
                    break;
            }
        }

        return statements;
    }

    // ------------------------------------------------------------------
    // BuildView
    // ------------------------------------------------------------------

    public static OntologyView BuildView(string graphIri, IReadOnlyList<RdfStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(graphIri);
        ArgumentNullException.ThrowIfNull(statements);

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

        // RDF list / owl:unionOf bookkeeping (port of schema.py _union_members /
        // _concrete_members). We need every domain / range value expanded
        // through any owl:unionOf blank node so multi-valued or union-shaped
        // properties don't collapse to one arbitrary last-writer value.
        var unionHead = new Dictionary<string, string>(StringComparer.Ordinal);
        var listFirst = new Dictionary<string, string>(StringComparer.Ordinal);
        var listRest = new Dictionary<string, string>(StringComparer.Ordinal);
        var bnodeSubjects = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in statements)
        {
            var siri = TermIri(s.Subject);
            var piri = s.PredicateIri;
            var oiri = TermIri(s.Object);

            if (piri == Vocabulary.RdfType)
            {
                if (oiri == Vocabulary.OwlClass && s.Subject is RdfIri)
                {
                    if (!classes.ContainsKey(siri))
                    {
                        classes[siri] = new ClassView(
                            Iri: siri, Local: LocalOf(siri), Label: "", Comment: "",
                            Superclasses: new List<string>());
                    }
                }
                else if (oiri == Vocabulary.OwlObjectProperty)
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
                else if (oiri == Vocabulary.OwlDatatypeProperty)
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
            else if (piri == Vocabulary.RdfsLabel)
            {
                if (s.Object is RdfLiteral lbl)
                {
                    labels[siri] = lbl.Value;
                }
            }
            else if (piri == Vocabulary.RdfsComment)
            {
                if (s.Object is RdfLiteral cmt)
                {
                    comments[siri] = cmt.Value;
                }
            }
            else if (piri == Vocabulary.RdfsSubClassOf)
            {
                subClassOf.Add(new AxiomPair(siri, oiri));
            }
            else if (piri == Vocabulary.RdfsDomain)
            {
                domains[siri] = oiri;
                if (!domainAll.TryGetValue(siri, out var list))
                {
                    list = new List<string>();
                    domainAll[siri] = list;
                }
                list.Add(oiri);
            }
            else if (piri == Vocabulary.RdfsRange)
            {
                ranges[siri] = oiri;
                if (!rangeAll.TryGetValue(siri, out var list))
                {
                    list = new List<string>();
                    rangeAll[siri] = list;
                }
                list.Add(oiri);
            }
            else if (piri == Vocabulary.OwlDisjointWith)
            {
                disjoint.Add(new AxiomPair(siri, oiri));
            }
            else if (piri == Vocabulary.OwlEquivalentClass)
            {
                equivalent.Add(new AxiomPair(siri, oiri));
            }
            else if (piri == Vocabulary.OwlUnionOf)
            {
                // The subject is the anonymous union bnode; the object is the
                // head cell of its rdf:List.
                unionHead[siri] = oiri;
            }
            else if (piri == Vocabulary.RdfFirst)
            {
                listFirst[siri] = oiri;
            }
            else if (piri == Vocabulary.RdfRest)
            {
                listRest[siri] = oiri;
            }

            if (s.Subject is RdfBlankNode)
            {
                bnodeSubjects.Add(siri);
            }
        }

        return FinalizeView(classes, objProps, dataProps, labels, comments, subClassOf,
            disjoint, equivalent, domains, ranges, domainAll, rangeAll,
            unionHead, listFirst, listRest);
    }

    private static OntologyView FinalizeView(
        Dictionary<string, ClassView> classes,
        Dictionary<string, PropertyView> objProps,
        Dictionary<string, PropertyView> dataProps,
        Dictionary<string, string> labels,
        Dictionary<string, string> comments,
        List<AxiomPair> subClassOf,
        List<AxiomPair> disjoint,
        List<AxiomPair> equivalent,
        Dictionary<string, string> domains,
        Dictionary<string, string> ranges,
        Dictionary<string, List<string>> domainAll,
        Dictionary<string, List<string>> rangeAll,
        Dictionary<string, string> unionHead,
        Dictionary<string, string> listFirst,
        Dictionary<string, string> listRest)
    {
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
            .Select(iri => PropEntry(iri, isDatatypeRange: false, labels, comments, domains, ranges, domainAll, rangeAll, unionHead, listFirst, listRest, LabelOf))
            .ToList();
        var dataList = dataProps.Keys
            .OrderBy(LabelOf, StringComparer.Ordinal)
            .Select(iri => PropEntry(iri, isDatatypeRange: true, labels, comments, domains, ranges, domainAll, rangeAll, unionHead, listFirst, listRest, LabelOf))
            .ToList();

        return new OntologyView(
            Classes: classList,
            ObjectProperties: objList,
            DataProperties: dataList,
            Axioms: new AxiomView(subClassOf, disjoint, equivalent));
    }

    // BuildMutationStatements is a pure projection (it returns statements
    // but doesn't write); the graph IRI is a parameter so the emitted
    // statements land in the caller's graph, not a placeholder.

    private static string LocalOf(string iri) =>
        iri.Contains('#') ? iri[(iri.LastIndexOf('#') + 1)..] : iri.TrimEnd('/').Split('/')[^1];

    private static string TermIri(RdfTerm term) => term switch
    {
        RdfIri n => n.Value,
        RdfBlankNode b => b.Id,
        RdfLiteral l => l.Value,
        _ => term.ToString() ?? "",
    };

    /// <summary>
    /// Walk an rdf:List whose head is <paramref name="headValue"/>, returning
    /// the IRIs of every member cell. Stops at <c>rdf:nil</c> and bails out
    /// after 1000 hops to defend against cyclic graphs. Mirrors
    /// <c>_union_members</c> in schema.py.
    /// </summary>
    private static List<string> UnionMembers(
        string? headValue,
        IReadOnlyDictionary<string, string> listFirst,
        IReadOnlyDictionary<string, string> listRest)
    {
        var outList = new List<string>();
        if (headValue is null) return outList;
        var cur = headValue;
        int guard = 0;
        while (!string.IsNullOrEmpty(cur)
            && cur != Vocabulary.RdfNil
            && guard < 1000)
        {
            if (listFirst.TryGetValue(cur, out var first))
            {
                outList.Add(first);
            }
            listRest.TryGetValue(cur, out cur);
            guard++;
        }
        return outList;
    }

    /// <summary>
    /// Flatten a property's domain/range value(s) to the concrete class /
    /// datatype IRIs they admit: every rdfs:domain / rdfs:range triple, with
    /// any owl:unionOf expanded to its members. De-duplicated,
    /// order-preserving. Mirrors <c>_concrete_members</c> in schema.py.
    /// </summary>
    private static List<string> ConcreteMembers(
        IReadOnlyList<string> values,
        IReadOnlyDictionary<string, string> unionHead,
        IReadOnlyDictionary<string, string> listFirst,
        IReadOnlyDictionary<string, string> listRest)
    {
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            var members = unionHead.ContainsKey(v)
                ? UnionMembers(v, listFirst, listRest)
                : new List<string> { v };
            foreach (var m in members)
            {
                if (seen.Add(m)) outList.Add(m);
            }
        }
        return outList;
    }

    private static PropertyView PropEntry(
        string iri,
        bool isDatatypeRange,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> comments,
        IReadOnlyDictionary<string, string> domains,
        IReadOnlyDictionary<string, string> ranges,
        IReadOnlyDictionary<string, List<string>> domainAll,
        IReadOnlyDictionary<string, List<string>> rangeAll,
        IReadOnlyDictionary<string, string> unionHead,
        IReadOnlyDictionary<string, string> listFirst,
        IReadOnlyDictionary<string, string> listRest,
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
        var dMembers = ConcreteMembers(
            domainAll.TryGetValue(iri, out var dList) ? dList : Array.Empty<string>(),
            unionHead, listFirst, listRest);
        var rMembers = ConcreteMembers(
            rangeAll.TryGetValue(iri, out var rList) ? rList : Array.Empty<string>(),
            unionHead, listFirst, listRest);
        return new PropertyView(
            Iri: iri,
            Local: LocalOf(iri),
            Label: labels.TryGetValue(iri, out var l) ? l : LocalOf(iri),
            Comment: comments.TryGetValue(iri, out var c) ? c : "",
            Domain: domains.TryGetValue(iri, out var d) ? d : null,
            DomainLabel: domainLabel,
            Range: ranges.TryGetValue(iri, out var r) ? r : null,
            RangeLabel: rangeLabel,
            DomainMembers: dMembers,
            RangeMembers: rMembers);
    }
}