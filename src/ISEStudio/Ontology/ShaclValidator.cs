using Oxigraph;
using ISEStudio.Observability;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoTerm = Oxigraph.INamedOrBlankNode;

namespace ISEStudio.Ontology;

/// <summary>SHACL namespace constants.</summary>
public static class ShaclVocab
{
    public const string Shacl = "http://www.w3.org/ns/shacl#";

    public static readonly OntoNamedNode NodeShape = new(Shacl + "NodeShape");
    public static readonly OntoNamedNode PropertyShape = new(Shacl + "PropertyShape");
    public static readonly OntoNamedNode TargetClass = new(Shacl + "targetClass");
    public static readonly OntoNamedNode Property = new(Shacl + "property");
    public static readonly OntoNamedNode Path = new(Shacl + "path");
    public static readonly OntoNamedNode MinCount = new(Shacl + "minCount");
    public static readonly OntoNamedNode Datatype = new(Shacl + "datatype");
    public static readonly OntoNamedNode NodeKind = new(Shacl + "nodeKind");
    public static readonly OntoNamedNode Class = new(Shacl + "class");
    public static readonly OntoNamedNode Iri = new(Shacl + "IRI");
    public static readonly OntoNamedNode LiteralKind = new(Shacl + "Literal");
    public static readonly OntoNamedNode Message = new(Shacl + "message");
    public static readonly OntoNamedNode Severity = new(Shacl + "severity");
    public static readonly OntoNamedNode Violation = new(Shacl + "Violation");
    public static readonly OntoNamedNode SourceShape = new(Shacl + "sourceShape");
    public static readonly OntoNamedNode SourceConstraintComponent = new(Shacl + "sourceConstraintComponent");
}

/// <summary>One violation surfaced by <see cref="ShaclValidator.Validate"/>.</summary>
public sealed record ShaclViolation(
    string SourceShapeIri,
    string FocusNodeIri,
    string ResultPathIri,
    string ValueKind,
    string Message);

/// <summary>Aggregate SHACL report.</summary>
public sealed record ShaclReport(
    bool Conforms,
    IReadOnlyList<ShaclViolation> Violations);

/// <summary>
/// Hand-rolled SHACL validator covering the subset of W3C SHACL Core that
/// the ISEStudio shapes use:
/// <list type="bullet">
/// <item><c>sh:NodeShape</c> + <c>sh:targetClass</c> for shape targeting.</item>
/// <item><c>sh:property</c> (top-level only — no nested <c>sh:NodeShape</c>
/// references through <c>sh:node</c>) with
/// <c>sh:path</c>, <c>sh:minCount</c>, <c>sh:datatype</c>,
/// <c>sh:nodeKind</c>, and <c>sh:class</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>This is intentionally NOT a full W3C SHACL implementation; Oxigraph
/// 0.5.8 has no built-in SHACL validator and bundling a third-party one
/// would expand the dependency surface significantly. The shapes we ship
/// in <c>Shapes/tbox-shapes.ttl</c> use only this subset, and the
/// authoritative role-evidence and normalization logic continues to live
/// in <see cref="Guard"/>.</para>
/// </remarks>
public sealed class ShaclValidator
{
    private readonly StoreWrapper _shapeStore;
    private readonly StoreWrapper _dataStore;

    /// <param name="shapeStore">Store containing the <c>sh:NodeShape</c>
    /// definitions. Typically loaded from <c>Shapes/tbox-shapes.ttl</c>.</param>
    /// <param name="dataStore">Store containing the data graph to validate.</param>
    public ShaclValidator(StoreWrapper shapeStore, StoreWrapper dataStore)
    {
        ArgumentNullException.ThrowIfNull(shapeStore);
        ArgumentNullException.ThrowIfNull(dataStore);
        _shapeStore = shapeStore;
        _dataStore = dataStore;
    }

    /// <summary>
    /// Validate the data in <paramref name="dataGraphIri"/> against every
    /// shape in the shape store. Empty list of violations means the data
    /// conforms to every shape.
    /// </summary>
    public ShaclReport Validate(string dataGraphIri)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataGraphIri);

        return Telemetry.RdfSource.WithShaclActivity(
            "rdf.shacl.validate",
            dataGraphIri,
            () =>
            {
                var shapes = ReadShapes();
                var violations = new List<ShaclViolation>();
                foreach (var shape in shapes)
                {
                    foreach (var target in shape.TargetClasses)
                    {
                        violations.AddRange(ValidateShape(shape, target, dataGraphIri));
                    }
                }
                return new ShaclReport(violations.Count == 0, violations);
            }).Report;
    }

    // ------------------------------------------------------------------
    // Shape ingestion
    // ------------------------------------------------------------------

    private sealed record ShapeDef(
        string Iri,
        IReadOnlyList<string> TargetClasses,
        IReadOnlyList<PropertyShapeDef> Properties);

    private sealed record PropertyShapeDef(
        string Id,
        string PathIri,
        int? MinCount,
        string? DatatypeIri,
        string? NodeKind,
        string? ClassIri,
        string? Message);

    private List<ShapeDef> ReadShapes()
    {
        var result = new List<ShapeDef>();
        var shapeIris = new HashSet<string>(StringComparer.Ordinal);

        // Step 1: collect every sh:NodeShape subject. Shapes are always
        // named resources; property shapes (their sh:property values) are
        // commonly blank nodes in Turtle form, but that doesn't change
        // shape identification.
        // Use the OntoNamedNode-based Match overload (passing null graph)
        // so Oxigraph treats `graph == null` as a wildcard across all named
        // graphs — not as a filter on the default graph.
        var shPredicate = (OntoNamedNode?)new OntoNamedNode(ShaclVocab.TargetClass.Value);
        foreach (var q in _shapeStore.Match(predicate: shPredicate))
        {
            if (q.Subject is OntoNamedNode s && q.Object is OntoNamedNode t)
            {
                shapeIris.Add(s.Value);
            }
        }
        var rdfTypePredicate = (OntoNamedNode?)new OntoNamedNode(Vocabulary.RdfType.Value);
        foreach (var q in _shapeStore.Match(predicate: rdfTypePredicate))
        {
            if (q.Subject is OntoNamedNode s
                && q.Object is OntoNamedNode t
                && t.Value == ShaclVocab.NodeShape.Value)
            {
                shapeIris.Add(s.Value);
            }
        }

        // Step 2: per shape, gather target classes + property shapes.
        // Property shapes can be blank nodes in Turtle form — accept BOTH
        // named nodes and blank nodes so shapes like
        //   op:X a sh:NodeShape ; sh:property [ sh:path ... ] .
        // are recognized.
        foreach (var shapeIri in shapeIris)
        {
            var targets = new List<string>();
            var propertyShapes = new List<OntoTerm>();
            foreach (var q in _shapeStore.Match(
                subject: (OntoNamedNode?)new OntoNamedNode(shapeIri)))
            {
                if (q.Predicate.Value == ShaclVocab.TargetClass.Value && q.Object is OntoNamedNode t)
                    targets.Add(t.Value);
                if (q.Predicate.Value == ShaclVocab.Property.Value
                    && q.Object is OntoTerm psTerm)
                {
                    propertyShapes.Add(psTerm);
                }
            }
            var props = new List<PropertyShapeDef>();
            foreach (var ps in propertyShapes)
            {
                var prop = ReadPropertyShape(ps);
                if (prop is not null) props.Add(prop);
            }
            result.Add(new ShapeDef(shapeIri, targets, props));
        }
        return result;
    }

    private PropertyShapeDef? ReadPropertyShape(OntoTerm psTerm)
    {
        string? path = null;
        int? minCount = null;
        string? datatype = null;
        string? nodeKind = null;
        string? cls = null;
        string? message = null;
        foreach (var q in _shapeStore.MatchSubject(psTerm))
        {
            switch (q.Predicate.Value)
            {
                case "http://www.w3.org/ns/shacl#path":
                    if (q.Object is OntoNamedNode n) path = n.Value;
                    break;
                case "http://www.w3.org/ns/shacl#minCount":
                    if (q.Object is OntoLiteral l && int.TryParse(l.Value, out var minValue)) minCount = minValue;
                    break;
                case "http://www.w3.org/ns/shacl#datatype":
                    if (q.Object is OntoNamedNode d) datatype = d.Value;
                    break;
                case "http://www.w3.org/ns/shacl#nodeKind":
                    if (q.Object is OntoNamedNode k) nodeKind = k.Value;
                    break;
                case "http://www.w3.org/ns/shacl#class":
                    if (q.Object is OntoNamedNode c) cls = c.Value;
                    break;
                case "http://www.w3.org/ns/shacl#message":
                    if (q.Object is OntoLiteral m) message = m.Value;
                    break;
            }
        }
        if (path is null) return null;
        // Use the blank-node label (or named IRI) as the stable identifier
        // for violation reporting. Oxigraph assigns blank node labels at
        // load time and they don't change for the lifetime of the store.
        var id = psTerm is OntoNamedNode nn ? nn.Value : ((OntoBlankNode)psTerm).Value;
        return new PropertyShapeDef(id, path, minCount, datatype, nodeKind, cls, message);
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private IEnumerable<ShaclViolation> ValidateShape(ShapeDef shape, string targetClass, string dataGraphIri)
    {
        var data = _dataStore.Match(graphIri: dataGraphIri);
        var focusNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in data)
        {
            if (q.Predicate.Value == Vocabulary.RdfType.Value
                && q.Object is OntoNamedNode t
                && t.Value == targetClass
                && q.Subject is OntoNamedNode s)
            {
                focusNodes.Add(s.Value);
            }
        }
        foreach (var focus in focusNodes)
        {
            foreach (var prop in shape.Properties)
            {
                foreach (var v in ValidateProperty(focus, prop, dataGraphIri))
                    yield return v;
            }
        }
    }

    private IEnumerable<ShaclViolation> ValidateProperty(string focus, PropertyShapeDef prop, string dataGraphIri)
    {
        var values = _dataStore.Match(subjectIri: focus, predicateIri: prop.PathIri, graphIri: dataGraphIri);
        if (prop.MinCount is int minCount && values.Count < minCount)
        {
            yield return new ShaclViolation(
                SourceShapeIri: prop.Id,
                FocusNodeIri: focus,
                ResultPathIri: prop.PathIri,
                ValueKind: "missing",
                Message: prop.Message ?? $"Property {prop.PathIri} requires at least {prop.MinCount} value(s) on {focus}.");
        }
        foreach (var q in values)
        {
            string kind = q.Object switch
            {
                OntoNamedNode => "iri",
                OntoBlankNode => "blank",
                OntoLiteral => "literal",
                _ => "unknown",
            };
            if (prop.NodeKind is { } nk)
            {
                bool ok = (nk == ShaclVocab.Iri.Value && kind == "iri")
                    || (nk == ShaclVocab.LiteralKind.Value && kind == "literal");
                if (!ok)
                {
                    yield return new ShaclViolation(
                        SourceShapeIri: prop.Id,
                        FocusNodeIri: focus,
                        ResultPathIri: prop.PathIri,
                        ValueKind: kind,
                        Message: prop.Message ?? $"Property {prop.PathIri} value is not of nodeKind {nk}.");
                }
            }
            if (prop.DatatypeIri is { } dt
                && q.Object is OntoLiteral lit
                && (lit.Datatype?.Value ?? "http://www.w3.org/2001/XMLSchema#string") != dt)
            {
                yield return new ShaclViolation(
                    SourceShapeIri: prop.Id,
                    FocusNodeIri: focus,
                    ResultPathIri: prop.PathIri,
                    ValueKind: "literal",
                    Message: prop.Message ?? $"Property {prop.PathIri} value does not match datatype {dt}.");
            }
            if (prop.ClassIri is { } cls && kind == "iri")
            {
                // Verify the value has an rdf:type that includes cls (transitively not enforced).
                var types = _dataStore.Match(subjectIri: ((OntoNamedNode)q.Object).Value,
                    predicateIri: Vocabulary.RdfType.Value, graphIri: dataGraphIri);
                var hasClass = types.Any(tq => tq.Object is OntoNamedNode tn && tn.Value == cls);
                if (!hasClass)
                {
                    yield return new ShaclViolation(
                        SourceShapeIri: prop.Id,
                        FocusNodeIri: focus,
                        ResultPathIri: prop.PathIri,
                        ValueKind: "iri",
                        Message: prop.Message ?? $"Property {prop.PathIri} value is not typed as {cls}.");
                }
            }
        }
    }
}