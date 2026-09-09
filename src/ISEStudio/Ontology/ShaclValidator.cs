using ISEStudio.Observability;

namespace ISEStudio.Ontology;

/// <summary>SHACL namespace constants.</summary>
public static class ShaclVocab
{
    public const string Shacl = "http://www.w3.org/ns/shacl#";

    public const string NodeShape = Shacl + "NodeShape";
    public const string PropertyShape = Shacl + "PropertyShape";
    public const string TargetClass = Shacl + "targetClass";
    public const string Property = Shacl + "property";
    public const string Path = Shacl + "path";
    public const string MinCount = Shacl + "minCount";
    public const string Datatype = Shacl + "datatype";
    public const string NodeKind = Shacl + "nodeKind";
    public const string Class = Shacl + "class";
    public const string Iri = Shacl + "IRI";
    public const string LiteralKind = Shacl + "Literal";
    public const string Message = Shacl + "message";
    public const string Severity = Shacl + "severity";
    public const string Violation = Shacl + "Violation";
    public const string SourceShape = Shacl + "sourceShape";
    public const string SourceConstraintComponent = Shacl + "sourceConstraintComponent";
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
/// <para>This is intentionally NOT a full W3C SHACL implementation —
/// bundling a third-party one would expand the dependency surface
/// significantly. The shapes we ship in <c>Shapes/tbox-shapes.ttl</c> use
/// only this subset, and the authoritative role-evidence and
/// normalization logic continues to live in <see cref="Guard"/>.</para>
///
/// <para>The algorithm hot path is defined on
/// <see cref="RdfStatement"/>; the validator consumes the PostgreSQL
/// statement layer directly.</para>
/// </remarks>
public sealed class ShaclValidator
{
    private readonly IReadOnlyList<RdfStatement> _shapeStatements;
    private readonly IReadOnlyList<RdfStatement> _dataStatements;

    /// <summary>
    /// Accepts the shape graph and the data graph as
    /// <see cref="RdfStatement"/> lists. This is the runtime boundary:
    /// the Postgres statement store returns <see cref="RdfStatement"/>
    /// directly.
    /// </summary>
    public ShaclValidator(IReadOnlyList<RdfStatement> shapeStatements,
        IReadOnlyList<RdfStatement> dataStatements)
    {
        _shapeStatements = shapeStatements ?? throw new ArgumentNullException(nameof(shapeStatements));
        _dataStatements = dataStatements ?? throw new ArgumentNullException(nameof(dataStatements));
    }

    private static IEnumerable<RdfStatement> Match(
        IEnumerable<RdfStatement> statements,
        string? graphIri = null,
        RdfIri? subject = null,
        string? predicate = null) =>
        statements
            .Where(s => graphIri is null || s.GraphIri == graphIri)
            .Where(s => subject is null || s.Subject.Equals(subject))
            .Where(s => predicate is null || s.PredicateIri == predicate);

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
        // Pass null graph so RdfStatement treats it as a wildcard across
        // all named graphs — not as a filter on the default graph.
        foreach (var s in Match(_shapeStatements, predicate: ShaclVocab.TargetClass))
        {
            if (s.Subject is RdfIri si && s.Object is RdfIri ti)
            {
                shapeIris.Add(si.Value);
            }
        }
        foreach (var s in Match(_shapeStatements, predicate: Vocabulary.RdfType))
        {
            if (s.Subject is RdfIri si
                && s.Object is RdfIri ti
                && ti.Value == ShaclVocab.NodeShape)
            {
                shapeIris.Add(si.Value);
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
            var propertyShapes = new List<RdfTerm>();
            var shapeNode = new RdfIri(shapeIri);
            foreach (var s in Match(_shapeStatements, subject: shapeNode))
            {
                if (s.PredicateIri == ShaclVocab.TargetClass && s.Object is RdfIri ti)
                    targets.Add(ti.Value);
                if (s.PredicateIri == ShaclVocab.Property
                    && (s.Object is RdfIri || s.Object is RdfBlankNode))
                {
                    propertyShapes.Add(s.Object);
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

    private PropertyShapeDef? ReadPropertyShape(RdfTerm psTerm)
    {
        string? path = null;
        int? minCount = null;
        string? datatype = null;
        string? nodeKind = null;
        string? cls = null;
        string? message = null;
        foreach (var s in _shapeStatements.Where(s => s.Subject.Equals(psTerm)))
        {
            switch (s.PredicateIri)
            {
                case ShaclVocab.Path:
                    if (s.Object is RdfIri n) path = n.Value;
                    break;
                case ShaclVocab.MinCount:
                    if (s.Object is RdfLiteral l && int.TryParse(l.Value, out var minValue)) minCount = minValue;
                    break;
                case ShaclVocab.Datatype:
                    if (s.Object is RdfIri d) datatype = d.Value;
                    break;
                case ShaclVocab.NodeKind:
                    if (s.Object is RdfIri k) nodeKind = k.Value;
                    break;
                case ShaclVocab.Class:
                    if (s.Object is RdfIri c) cls = c.Value;
                    break;
                case ShaclVocab.Message:
                    if (s.Object is RdfLiteral m) message = m.Value;
                    break;
            }
        }
        if (path is null) return null;
        // Use the blank-node label (or named IRI) as the stable identifier
        // for violation reporting. RdfStatement blank-node labels are
        // preserved verbatim from the source so they remain stable.
        var id = psTerm is RdfIri nn ? nn.Value : ((RdfBlankNode)psTerm).Id;
        return new PropertyShapeDef(id, path, minCount, datatype, nodeKind, cls, message);
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private IEnumerable<ShaclViolation> ValidateShape(ShapeDef shape, string targetClass, string dataGraphIri)
    {
        var data = Match(_dataStatements, graphIri: dataGraphIri);
        var focusNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in data)
        {
            if (s.PredicateIri == Vocabulary.RdfType
                && s.Object is RdfIri t
                && t.Value == targetClass
                && s.Subject is RdfIri si)
            {
                focusNodes.Add(si.Value);
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
        var values = _dataStatements.Where(s => s.GraphIri == dataGraphIri
            && s.Subject is RdfIri subject && subject.Value == focus
            && s.PredicateIri == prop.PathIri).ToList();
        if (prop.MinCount is int minCount && values.Count < minCount)
        {
            yield return new ShaclViolation(
                SourceShapeIri: prop.Id,
                FocusNodeIri: focus,
                ResultPathIri: prop.PathIri,
                ValueKind: "missing",
                Message: prop.Message ?? $"Property {prop.PathIri} requires at least {prop.MinCount} value(s) on {focus}.");
        }
        foreach (var s in values)
        {
            string kind = s.Object switch
            {
                RdfIri => "iri",
                RdfBlankNode => "blank",
                RdfLiteral => "literal",
                _ => "unknown",
            };
            if (prop.NodeKind is { } nk)
            {
                bool ok = (nk == ShaclVocab.Iri && kind == "iri")
                    || (nk == ShaclVocab.LiteralKind && kind == "literal");
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
                && s.Object is RdfLiteral lit
                && (lit.Datatype ?? "http://www.w3.org/2001/XMLSchema#string") != dt)
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
                var types = _dataStatements.Where(ts => ts.GraphIri == dataGraphIri
                    && ts.Subject is RdfIri subject && subject.Value == ((RdfIri)s.Object).Value
                    && ts.PredicateIri == Vocabulary.RdfType).ToList();
                var hasClass = types.Any(ts => ts.Object is RdfIri tn && tn.Value == cls);
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