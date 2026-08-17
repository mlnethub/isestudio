using System.Text.RegularExpressions;

namespace OnToPilot.Ontology;

/// <summary>
/// Carries the source text plus the existing-graph norms the Guard needs to
/// decide which classes / properties a delta is allowed to introduce.
/// Equivalent to the keyword arguments of the Python
/// <c>sanitize_ontology_delta</c>.
/// </summary>
public sealed record GuardContext(
    string SourceText,
    IReadOnlyCollection<string>? ExistingClassNorms = null,
    IReadOnlyDictionary<string, string>? StructuredNonTypeSignals = null,
    string? CorpusRoleSourceText = null,
    IReadOnlyCollection<string>? ExistingObjectPropertyNorms = null,
    IReadOnlyCollection<string>? ExistingDataPropertyNorms = null);

/// <summary>
/// One entry that was rejected out of an <see cref="OntologyMutation"/>.
/// <see cref="Label"/> is the surface label; <see cref="Reason"/> is the
/// generic domain-neutral explanation ("XML Schema datatype is a literal
/// range, not an OWL class", etc.).
/// </summary>
public sealed record RejectedEntity(string Label, string Reason);

/// <summary>
/// Result of <see cref="Guard.Sanitize"/>: the cleaned ontology mutation plus
/// the labels that were rejected out of it. <see cref="Individuals"/> is the
/// list of rejected labels (typically ABox-style named entities that should
/// not have appeared in a TBox delta); <see cref="Classes"/>,
/// <see cref="ObjectProperties"/>, <see cref="DataProperties"/>, and
/// <see cref="Axioms"/> are the sanitized input the caller is allowed to
/// apply.
/// </summary>
public sealed record GuardResult(
    IReadOnlyList<ClassMutation> Classes,
    IReadOnlyList<PropertyMutation> ObjectProperties,
    IReadOnlyList<PropertyMutation> DataProperties,
    IReadOnlyList<AxiomMutation> Axioms,
    IReadOnlyList<RejectedEntity> Individuals);

/// <summary>
/// Domain-neutral structural checks for model-produced ontology mutations.
/// Mirrors <c>backend/app/ontology/tbox_guard.py</c>. No ontology-domain
/// vocabulary lives here: only generic structured-data provenance and the
/// lexical rules for safe sub-classing. A separate role critic decides
/// semantics; the Guard enforces ontology invariants.
/// </summary>
public static class Guard
{
    // Property names that look like a relation but carry no domain/range
    // payload (mirrors _CONTENT_FREE_PROPERTY_NAMES).
    private static readonly HashSet<string> ContentFreePropertyNames = new(StringComparer.Ordinal)
    {
        "has", "have",
    };

    // Local names that should be treated as XSD datatypes (mirrors
    // _BARE_DATATYPE_CLASS_NAMES).
    private static readonly HashSet<string> BareDatatypeClassNames = new(StringComparer.Ordinal)
    {
        "string", "integer", "decimal", "boolean", "date", "datetime", "time", "anyuri",
    };

    // ------------------------------------------------------------------
    // Normalization
    // ------------------------------------------------------------------

    /// <summary>
    /// Internal class-label normalization: NFKC, casefold, underscore → space,
    /// word-extract (preserves non-ASCII letters so CJK / accented labels
    /// stay distinct).
    /// </summary>
    private static string Normalize(string? value)
    {
        value = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('_', ' ');
        return string.Join(' ', Regex.Matches(value, @"\w+").Cast<Match>().Select(m => m.Value));
    }

    // ------------------------------------------------------------------
    // Datatype name resolution
    // ------------------------------------------------------------------

    /// <summary>Return true iff a label looks like an XSD datatype, not a class.</summary>
    private static bool IsDatatypeClassLabel(string value)
    {
        var token = value.Normalize(System.Text.NormalizationForm.FormKC).Trim().Trim('<', '>').ToLowerInvariant();
        var isPrefixed = token.StartsWith("xsd:") || token.Contains("xmlschema#", StringComparison.OrdinalIgnoreCase);
        if (!isPrefixed && !BareDatatypeClassNames.Contains(token)) return false;
        return Vocabulary.CanonicalDatatypeName(value) is not null;
    }

    // ------------------------------------------------------------------
    // Subclass lexical safety
    // ------------------------------------------------------------------

    private static bool CompoundHeadMismatch(string subL, string parentL)
    {
        var subTokens = Normalize(subL).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parentTokens = Normalize(parentL).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (subTokens.Length <= parentTokens.Length || parentTokens.Length == 0) return false;
        // Every sub-token must contain at least one ASCII letter; CJK /
        // accented labels skip this rule.
        foreach (var t in subTokens)
        {
            bool hasAsciiAlpha = false;
            foreach (var c in t)
            {
                if (c < 128 && char.IsLetter(c)) { hasAsciiAlpha = true; break; }
            }
            if (!hasAsciiAlpha) return false;
        }
        var parentSet = new HashSet<string>(parentTokens);
        var subSet = new HashSet<string>(subTokens);
        if (!parentSet.IsSubsetOf(subSet)) return false;
        return subTokens[^1] != parentTokens[^1];
    }

    private static bool IsLexicallySafeSubclass(string subL, string parentL)
    {
        var subTokens = Normalize(subL).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parentTokens = Normalize(parentL).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (subTokens.Length <= parentTokens.Length || parentTokens.Length == 0) return false;
        foreach (var t in subTokens)
        {
            bool hasAsciiAlpha = false;
            foreach (var c in t)
            {
                if (c < 128 && char.IsLetter(c)) { hasAsciiAlpha = true; break; }
            }
            if (!hasAsciiAlpha) return false;
        }
        for (int i = 0; i < parentTokens.Length; i++)
        {
            if (subTokens[subTokens.Length - parentTokens.Length + i] != parentTokens[i]) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Reference extraction
    // ------------------------------------------------------------------

    private static string First(IReadOnlyDictionary<string, object?> row, params string[] fields)
    {
        foreach (var f in fields)
        {
            if (row.TryGetValue(f, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
        }
        return "";
    }

    private static List<string> ClassReferences(OntologyMutation ontology)
    {
        var references = new List<string>();

        // Classes — class label.
        foreach (var c in ontology.Classes ?? Array.Empty<ClassMutation>())
        {
            if (!string.IsNullOrWhiteSpace(c.Label)) references.Add(c.Label);
        }

        // Object property — domain / range.
        foreach (var p in ontology.ObjectProperties ?? Array.Empty<PropertyMutation>())
        {
            if (!string.IsNullOrWhiteSpace(p.Domain)) references.Add(p.Domain);
            if (!string.IsNullOrWhiteSpace(p.Range)) references.Add(p.Range);
        }

        // Data property — domain (range is a datatype, not a class reference).
        foreach (var p in ontology.DataProperties ?? Array.Empty<PropertyMutation>())
        {
            if (!string.IsNullOrWhiteSpace(p.Domain)) references.Add(p.Domain);
        }

        // Subclass / disjoint / equivalent — sub/super/a/b.
        foreach (var a in ontology.Axioms ?? Array.Empty<AxiomMutation>())
        {
            if (!string.IsNullOrWhiteSpace(a.Sub)) references.Add(a.Sub);
            if (!string.IsNullOrWhiteSpace(a.Super)) references.Add(a.Super);
            if (!string.IsNullOrWhiteSpace(a.A)) references.Add(a.A);
            if (!string.IsNullOrWhiteSpace(a.B)) references.Add(a.B);
        }

        return references;
    }

    // ------------------------------------------------------------------
    // Sanitize
    // ------------------------------------------------------------------

    /// <summary>
    /// Enforce generic class / property / datatype invariants on a TBox
    /// delta. Rejects classes that look like ABox individuals or bare
    /// XSD datatypes, re-properties with no domain, and axioms whose
    /// endpoints are not available classes.
    /// </summary>
    public static GuardResult Sanitize(OntologyMutation ontology, GuardContext context)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(context);

        var structuredNonTypes = RoleEvidence.StructuredNonTypeValues(context.SourceText);
        var existingNorms = new HashSet<string>(
            (context.ExistingClassNorms ?? Enumerable.Empty<string>()).Select(Normalize),
            StringComparer.Ordinal);

        var classRows = ontology.Classes ?? Array.Empty<ClassMutation>();
        var verifiedNorms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in classRows)
        {
            if (row.RoleVerified && !string.IsNullOrWhiteSpace(row.Label))
            {
                verifiedNorms.Add(Normalize(row.Label));
            }
        }

        // 1. Decide which labels to block.
        var blocked = new Dictionary<string, RejectedEntity>(StringComparer.Ordinal);
        foreach (var label in ClassReferences(ontology))
        {
            var normalized = Normalize(label);
            var roleVerified = verifiedNorms.Contains(normalized) || existingNorms.Contains(normalized);
            string? reason = null;

            // Corpus-wide explicit identity evidence wins over local verdict.
            if (!string.IsNullOrEmpty(context.CorpusRoleSourceText)
                && RoleEvidence.HasExplicitIndividualDeclaration(context.CorpusRoleSourceText, label))
            {
                reason = "exact label is explicitly declared as an instance or individual elsewhere in the corpus";
            }
            if (reason is null && !roleVerified)
            {
                if (structuredNonTypes.TryGetValue(normalized, out var sreason))
                {
                    reason = sreason;
                }
                else if (context.StructuredNonTypeSignals is { } signals
                    && signals.TryGetValue(normalized, out var sigReason))
                {
                    reason = sigReason;
                }
            }
            if (IsDatatypeClassLabel(label))
            {
                reason = "XML Schema datatype is a literal range, not an OWL class";
            }
            if (reason is null
                && !existingNorms.Contains(normalized)
                && !RoleEvidence.SurfaceIsGrounded(context.SourceText, label))
            {
                reason = "new class label is not lexically grounded in the source";
            }
            if (reason is not null && !blocked.ContainsKey(normalized))
            {
                blocked[normalized] = new RejectedEntity(label, reason);
            }
        }

        bool IsBlocked(string? value) =>
            value is not null && blocked.ContainsKey(Normalize(value));

        // 2. Clean classes.
        var cleanedClasses = new List<ClassMutation>();
        foreach (var row in classRows)
        {
            if (IsBlocked(row.Label)) continue;
            cleanedClasses.Add(new ClassMutation(row.Label, row.Comment, RoleVerified: false));
        }

        var availableClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cleanedClasses)
        {
            if (!string.IsNullOrWhiteSpace(c.Label)) availableClasses.Add(Normalize(c.Label));
        }
        foreach (var n in existingNorms) availableClasses.Add(n);

        bool UnavailableClass(string? value) =>
            string.IsNullOrWhiteSpace(value) || !availableClasses.Contains(Normalize(value));

        // 3. Clean object / data properties.
        var existingObjectNorms = new HashSet<string>(
            (context.ExistingObjectPropertyNorms ?? Enumerable.Empty<string>()).Select(Normalize),
            StringComparer.Ordinal);
        var existingDataNorms = new HashSet<string>(
            (context.ExistingDataPropertyNorms ?? Enumerable.Empty<string>()).Select(Normalize),
            StringComparer.Ordinal);

        var objectRows = ontology.ObjectProperties ?? Array.Empty<PropertyMutation>();
        var dataRows = ontology.DataProperties ?? Array.Empty<PropertyMutation>();

        var declaredObjectNorms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in objectRows)
        {
            if (string.IsNullOrWhiteSpace(row.Label)) continue;
            if (Vocabulary.CanonicalDatatypeName(row.Range) is null)
            {
                declaredObjectNorms.Add(Normalize(row.Label));
            }
        }

        var convertedDataRows = new List<PropertyMutation>();
        var cleanedObjectRows = new List<PropertyMutation>();
        foreach (var row in objectRows)
        {
            var cleaned = new PropertyMutation(
                Label: row.Label, Kind: row.Kind, Comment: row.Comment,
                Domain: row.Domain, Range: row.Range);
            var labelNorm = Normalize(cleaned.Label);
            if (ContentFreePropertyNames.Contains(labelNorm)) continue;

            var datatypeAlias = Vocabulary.CanonicalDatatypeName(cleaned.Range);
            if (datatypeAlias is not null)
            {
                // If this label is being introduced as an object property AND
                // not yet known as one, reclassify as a data property. The
                // Python version converts; we honour the conversion only when
                // the caller declared no conflict.
                if (!declaredObjectNorms.Contains(labelNorm)
                    && !existingObjectNorms.Contains(labelNorm))
                {
                    var newDomain = IsBlocked(cleaned.Domain) || UnavailableClass(cleaned.Domain)
                        ? null : cleaned.Domain;
                    convertedDataRows.Add(new PropertyMutation(
                        Label: cleaned.Label, Kind: "data",
                        Comment: cleaned.Comment, Domain: newDomain, Range: datatypeAlias));
                }
                continue;
            }

            // If the label is already a known data property and not an
            // existing object property, drop the duplicate.
            if (existingDataNorms.Contains(labelNorm) && !existingObjectNorms.Contains(labelNorm))
            {
                continue;
            }

            var domain = IsBlocked(cleaned.Domain) || UnavailableClass(cleaned.Domain) ? null : cleaned.Domain;
            var range = IsBlocked(cleaned.Range) || UnavailableClass(cleaned.Range) ? null : cleaned.Range;
            cleanedObjectRows.Add(new PropertyMutation(
                Label: cleaned.Label, Kind: cleaned.Kind,
                Comment: cleaned.Comment, Domain: domain, Range: range));
        }

        var cleanedDataRows = new List<PropertyMutation>();
        var allDataRows = new List<PropertyMutation>(dataRows);
        allDataRows.AddRange(convertedDataRows);
        var seenData = new HashSet<(string, string, string)>();
        foreach (var row in allDataRows)
        {
            var cleaned = new PropertyMutation(
                Label: row.Label, Kind: row.Kind, Comment: row.Comment,
                Domain: row.Domain, Range: row.Range);
            var labelNorm = Normalize(cleaned.Label);
            if (ContentFreePropertyNames.Contains(labelNorm)) continue;

            // Drop property entirely if it conflicts with an existing
            // object-property declaration.
            if (existingObjectNorms.Contains(labelNorm) && !existingDataNorms.Contains(labelNorm))
            {
                continue;
            }

            var domain = IsBlocked(cleaned.Domain) || UnavailableClass(cleaned.Domain) ? null : cleaned.Domain;
            var rawRange = cleaned.Range;
            var datatype = Vocabulary.CanonicalDatatypeName(rawRange);
            if (rawRange is not null && rawRange != "" && datatype is null)
            {
                continue;
            }
            var finalRange = datatype ?? "string";

            var sig = (labelNorm, Normalize(domain ?? ""), finalRange);
            if (!seenData.Contains(sig))
            {
                seenData.Add(sig);
                cleanedDataRows.Add(new PropertyMutation(
                    Label: cleaned.Label, Kind: "data",
                    Comment: cleaned.Comment, Domain: domain, Range: finalRange));
            }
        }

        // 4. Clean axioms.
        var cleanedAxioms = new List<AxiomMutation>();
        foreach (var ax in ontology.Axioms ?? Array.Empty<AxiomMutation>())
        {
            var endpoints = ax.Type switch
            {
                "subclass" => new[] { ax.Sub, ax.Super },
                "disjoint" or "equivalent" => new[] { ax.A, ax.B },
                _ => Array.Empty<string?>(),
            };
            if (endpoints.Length == 0) continue;
            if (endpoints.Any(IsBlocked)) continue;
            if (endpoints.Any(UnavailableClass)) continue;
            if (ax.Type == "subclass"
                && !string.IsNullOrWhiteSpace(ax.Sub)
                && !string.IsNullOrWhiteSpace(ax.Super)
                && CompoundHeadMismatch(ax.Sub, ax.Super))
            {
                continue;
            }
            cleanedAxioms.Add(ax);
        }

        var rejected = blocked.Values.ToList();
        return new GuardResult(
            Classes: cleanedClasses,
            ObjectProperties: cleanedObjectRows,
            DataProperties: cleanedDataRows,
            Axioms: cleanedAxioms,
            Individuals: rejected);
    }
}