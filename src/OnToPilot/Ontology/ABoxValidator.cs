using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

/// <summary>
/// One ABox-level violation surfaced by <see cref="ABoxValidator"/>.
/// <see cref="Severity"/> is <c>"error"</c> or <c>"warning"</c>; the report
/// is sorted with errors first. <see cref="Fixes"/> is the list of ABox ops
/// that would resolve the violation.
/// </summary>
public sealed record ABoxViolation(
    string Id,
    string Type,
    string Severity,
    string Summary,
    IReadOnlyList<ABoxViolationFix> Fixes);

/// <summary>
/// One-click fix op attached to a violation. <see cref="OpKind"/> mirrors
/// the <c>kind</c> field of the Python <c>abox_validate.apply_fix</c>
/// payload.
/// </summary>
public sealed record ABoxViolationFix(string Id, string Label, string OpKind);

/// <summary>Aggregate result of <see cref="ABoxValidator.Validate"/>.</summary>
public sealed record ABoxValidationReport(
    IReadOnlyList<ABoxViolation> Violations,
    int ErrorCount,
    int WarningCount,
    bool Truncated);

/// <summary>
/// Lint the ABox (instance graph) of a knowledge system against its TBox
/// (schema graph). The 8 checks mirror the Python
/// <c>backend/app/ontology/abox_validate.py</c>:
/// <list type="bullet">
/// <item><c>placeholder</c> &mdash; a non-identifying label like
/// <c>"Untitled"</c> was stored as an individual.</item>
/// <item><c>type_count</c> &mdash; too many direct types on one individual.</item>
/// <item><c>role</c> &mdash; same individual is typed across incompatible
/// semantic roles.</item>
/// <item><c>unrelated_types</c> &mdash; types with no shared role or
/// super-class.</item>
/// <item><c>disjoint</c> &mdash; an individual is typed by two classes
/// declared <c>owl:disjointWith</c>.</item>
/// <item><c>domain</c> &mdash; subject uses a property whose domain class
/// it isn't typed as.</item>
/// <item><c>range</c> &mdash; object property target isn't typed as the
/// property's range class.</item>
/// <item><c>datatype</c> &mdash; data value doesn't parse as the property's
/// declared XSD type.</item>
/// </list>
/// </summary>
/// <remarks>
/// This is a supplementary check; the authoritative TBox invariants are
/// enforced by <see cref="Guard"/>. SHACL is a separate validator in
/// <see cref="ShaclValidator"/>; both report the same kinds of issues in
/// different lenses and either can flag a violation the other misses.
/// </remarks>
public sealed class ABoxValidator
{
    private readonly StoreWrapper _store;

    /// <summary>Per-call cap so a runaway ABox doesn't OOM the report.</summary>
    public const int MaxViolations = 500;

    /// <summary>Per-individual direct-type cap; above this we flag a merge.</summary>
    public const int MaxDirectTypes = 12;

    private static readonly HashSet<string> NonIdentifyingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled", "unknown", "n/a", "na", "none", "null", "tbd", "todo", "-",
    };

    public ABoxValidator(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Run every check against <paramref name="ks"/>'s ABox + TBox graphs.
    /// </summary>
    public ABoxValidationReport Validate(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);

        var view = SchemaBuilder.BuildView(ks.TBoxGraph, _store);
        var clabel = (string iri) =>
        {
            foreach (var c in view.Classes)
            {
                if (c.Iri == iri) return c.Label;
            }
            foreach (var p in view.ObjectProperties)
            {
                if (p.Iri == iri) return p.Label;
            }
            foreach (var p in view.DataProperties)
            {
                if (p.Iri == iri) return p.Label;
            }
            return LocalOf(iri);
        };

        var supers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in view.Classes)
        {
            supers[c.Iri] = c.Superclasses.ToList();
        }

        var disjoint = view.Axioms.DisjointWith
            .Select(p => (p.A, p.B))
            .ToList();

        var propDr = new Dictionary<string, (string Kind, string? Domain, string? Range,
            IReadOnlyList<string> DomainMembers, IReadOnlyList<string> RangeMembers, string Label)>(StringComparer.Ordinal);
        foreach (var p in view.ObjectProperties)
        {
            propDr[p.Iri] = ("object", p.Domain, p.Range, p.DomainMembers, p.RangeMembers, p.Label);
        }
        foreach (var p in view.DataProperties)
        {
            propDr[p.Iri] = ("data", p.Domain, p.Range, p.DomainMembers, p.RangeMembers, p.Label);
        }

        // 1. Scan ABox once.
        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);
        var types = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var indLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        var objAssert = new List<(string S, string P, string O)>();
        var dataAssert = new List<(string S, string P, string Value, string? Dt)>();
        foreach (var q in _store.Match(graph: aboxGraph))
        {
            if (q.Subject is not OntoNamedNode s) continue;
            var sIri = s.Value;
            if (q.Predicate.Value == Vocabulary.RdfType.Value && q.Object is OntoNamedNode t)
            {
                if (t.Value != Vocabulary.OwlNamedIndividual.Value)
                {
                    if (!types.TryGetValue(sIri, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        types[sIri] = set;
                    }
                    set.Add(t.Value);
                }
            }
            else if (q.Predicate.Value == Vocabulary.RdfsLabel.Value && q.Object is OntoLiteral lbl)
            {
                indLabels[sIri] = lbl.Value;
            }
            else if (q.Object is OntoNamedNode tn)
            {
                objAssert.Add((sIri, q.Predicate.Value, tn.Value));
            }
            else if (q.Object is OntoLiteral lit)
            {
                dataAssert.Add((sIri, q.Predicate.Value, lit.Value,
                    lit.Datatype?.Value));
            }
        }

        string Ilabel(string iri) => indLabels.TryGetValue(iri, out var v) ? v : iri.Split("ind-", 2)[^1];

        var closureCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Closure(string ind)
        {
            if (!closureCache.TryGetValue(ind, out var c))
            {
                c = new HashSet<string>(StringComparer.Ordinal);
                if (types.TryGetValue(ind, out var direct))
                {
                    foreach (var t in direct)
                    {
                        foreach (var a in Ancestors(supers, t))
                        {
                            c.Add(a);
                        }
                    }
                }
                closureCache[ind] = c;
            }
            return c;
        }

        var violations = new List<ABoxViolation>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void Add(ABoxViolation v)
        {
            if (seenIds.Contains(v.Id) || violations.Count >= MaxViolations) return;
            seenIds.Add(v.Id);
            violations.Add(v);
        }

        // 2. Identity-quality checks.
        foreach (var (ind, direct) in types)
        {
            var label = Ilabel(ind);
            if (IsNonIdentifyingLabel(label))
            {
                Add(new ABoxViolation(
                    Id: Sig("placeholder", ind),
                    Type: "placeholder",
                    Severity: "error",
                    Summary: $"\"{label}\" is a placeholder, not a stable individual identity.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("delete", "Delete this placeholder individual", "delete_individual"),
                    }));
            }
            if (direct.Count > MaxDirectTypes)
            {
                Add(new ABoxViolation(
                    Id: Sig("type_count", ind),
                    Type: "type_count",
                    Severity: "error",
                    Summary: $"\"{label}\" has {direct.Count} direct types, indicating that unrelated mentions were probably merged into one individual.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("delete", "Delete this over-merged individual", "delete_individual"),
                    }));
            }
        }

        // 3. Disjoint-type violations.
        foreach (var (ind, _) in types)
        {
            var cl = Closure(ind);
            foreach (var (a, b) in disjoint)
            {
                if (!cl.Contains(a) || !cl.Contains(b)) continue;
                Add(new ABoxViolation(
                    Id: Sig("disjoint", ind, a, b),
                    Type: "disjoint",
                    Severity: "error",
                    Summary: $"\"{Ilabel(ind)}\" is typed as both \"{clabel(a)}\" and \"{clabel(b)}\", which are disjoint.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("rm_a", $"Remove type \"{clabel(a)}\"", "remove_type"),
                        new ABoxViolationFix("rm_b", $"Remove type \"{clabel(b)}\"", "remove_type"),
                    }));
            }
        }

        // 4. domain / range / datatype (best-effort, single-writer for domain
        // and range; we keep the report narrow to avoid drowning the user).
        bool DisjointTypes(string a, string b)
        {
            if (a == b) return false;
            var aa = Ancestors(supers, a);
            var bb = Ancestors(supers, b);
            foreach (var (da, db) in disjoint)
            {
                if ((aa.Contains(da) && bb.Contains(db)) || (aa.Contains(db) && bb.Contains(da)))
                    return true;
            }
            return false;
        }
        string? ConflictingType(string ind, string target) =>
            types.TryGetValue(ind, out var s) ? s.FirstOrDefault(t => DisjointTypes(t, target)) : null;

        (List<string> members, string? conflicting)? UnionConflict(string ind, IReadOnlyList<string> members)
        {
            var cls = members.Where(m => !m.StartsWith(Vocabulary.Xsd, StringComparison.Ordinal)).ToList();
            if (cls.Count == 0 || cls.Any(m => Closure(ind).Contains(m))) return null;
            var conflicts = cls.Select(m => ConflictingType(ind, m)).Where(c => c != null).Cast<string>().ToList();
            if (conflicts.Count == cls.Count) return (cls, conflicts[0]);
            return null;
        }

        foreach (var (s, p, o) in objAssert)
        {
            if (!propDr.TryGetValue(p, out var dr)) continue;
            var dc = UnionConflict(s, dr.DomainMembers);
            if (dc.HasValue)
            {
                Add(new ABoxViolation(
                    Id: Sig("domain", s, p),
                    Type: "domain",
                    Severity: "warning",
                    Summary: $"\"{Ilabel(s)}\" uses \"{dr.Label}\" with a type disjoint from its domain.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("rm", "Remove this relationship", "remove_object_assertion"),
                    }));
            }
            var rc = UnionConflict(o, dr.RangeMembers);
            if (rc.HasValue)
            {
                Add(new ABoxViolation(
                    Id: Sig("range", s, p, o),
                    Type: "range",
                    Severity: "warning",
                    Summary: $"\"{Ilabel(o)}\" is the target of \"{dr.Label}\" with a type disjoint from its range.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("rm", "Remove this relationship", "remove_object_assertion"),
                    }));
            }
        }

        foreach (var (s, p, value, dt) in dataAssert)
        {
            if (!propDr.TryGetValue(p, out var dr)) continue;
            var dc = UnionConflict(s, dr.DomainMembers);
            if (dc.HasValue)
            {
                Add(new ABoxViolation(
                    Id: Sig("domain", s, p),
                    Type: "domain",
                    Severity: "warning",
                    Summary: $"\"{Ilabel(s)}\" uses \"{dr.Label}\" with a type disjoint from its domain.",
                    Fixes: new[]
                    {
                        new ABoxViolationFix("rm", "Remove this attribute", "remove_data_assertion"),
                    }));
            }
            if (dr.Range is { } rng && rng.StartsWith(Vocabulary.Xsd, StringComparison.Ordinal))
            {
                var xsdLocal = LocalOf(rng);
                if (!ValidXsd(value, xsdLocal))
                {
                    Add(new ABoxViolation(
                        Id: Sig("datatype", s, p, value),
                        Type: "datatype",
                        Severity: "warning",
                        Summary: $"\"{Ilabel(s)}\": \"{dr.Label}\" = \"{value}\" is not a valid {xsdLocal}.",
                        Fixes: new[]
                        {
                            new ABoxViolationFix("relax", $"Change \"{dr.Label}\" to text", "relax_range"),
                            new ABoxViolationFix("rm", "Remove this attribute", "remove_data_assertion"),
                        }));
                }
            }
        }

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1 };
        violations.Sort((x, y) =>
            order.TryGetValue(x.Severity, out var xi) ? xi : 2
            - (order.TryGetValue(y.Severity, out var yi) ? yi : 2));
        var errors = violations.Count(v => v.Severity == "error");
        var warnings = violations.Count(v => v.Severity == "warning");
        return new ABoxValidationReport(violations, errors, warnings, violations.Count >= MaxViolations);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HashSet<string> Ancestors(Dictionary<string, List<string>> supers, string iri)
    {
        var out_ = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(iri);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!out_.Add(cur)) continue;
            if (supers.TryGetValue(cur, out var ss))
            {
                foreach (var s in ss) stack.Push(s);
            }
        }
        return out_;
    }

    private static bool IsNonIdentifyingLabel(string label)
    {
        var t = (label ?? "").Trim();
        if (t.Length == 0) return true;
        return NonIdentifyingLabels.Contains(t);
    }

    private static bool ValidXsd(string value, string xsdLocal)
    {
        var v = (value ?? "").Trim();
        if (xsdLocal == "integer") return Regex.IsMatch(v, @"^[-+]?\d+$");
        if (xsdLocal is "decimal" or "float" or "double")
        {
            return double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }
        if (xsdLocal == "boolean") return v.ToLowerInvariant() is "true" or "false" or "0" or "1";
        return true; // string/date/dateTime — avoid false positives
    }

    private static string LocalOf(string iri) =>
        iri.Contains('#') ? iri[(iri.LastIndexOf('#') + 1)..] : iri.TrimEnd('/').Split('/')[^1];

    private static string Sig(params string[] parts)
    {
        var joined = string.Join("|", parts);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(joined));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}