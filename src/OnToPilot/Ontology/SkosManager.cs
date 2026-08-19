using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

// ----------------------------------------------------------------------
// SKOS namespace constants
// ----------------------------------------------------------------------

/// <summary>SKOS, DCTERMS, and OnToPilot vocabulary constants for the
/// vocabulary layer.</summary>
public static class SkosVocab
{
    public const string Skos = "http://www.w3.org/2004/02/skos/core#";
    public const string Dcterms = "http://purl.org/dc/terms/";
    public const string Ontopilot = "http://ontopilot.local/vocab#";

    public static readonly OntoNamedNode ConceptScheme = new(Skos + "ConceptScheme");
    public static readonly OntoNamedNode Concept = new(Skos + "Concept");
    public static readonly OntoNamedNode InScheme = new(Skos + "inScheme");
    public static readonly OntoNamedNode PrefLabel = new(Skos + "prefLabel");
    public static readonly OntoNamedNode AltLabel = new(Skos + "altLabel");
    public static readonly OntoNamedNode HiddenLabel = new(Skos + "hiddenLabel");
    public static readonly OntoNamedNode Broader = new(Skos + "broader");
    public static readonly OntoNamedNode Related = new(Skos + "related");
    public static readonly OntoNamedNode Notation = new(Skos + "notation");
    public static readonly OntoNamedNode Definition = new(Skos + "definition");

    public static readonly OntoNamedNode DcTitle = new(Dcterms + "title");
    public static readonly OntoNamedNode DcDescription = new(Dcterms + "description");
    public static readonly OntoNamedNode DcCreated = new(Dcterms + "created");
    public static readonly OntoNamedNode DcModified = new(Dcterms + "modified");

    public static readonly OntoNamedNode OpDefaultLanguage = new(Ontopilot + "defaultLanguage");
    public static readonly OntoNamedNode OpStatus = new(Ontopilot + "status");
    public static readonly OntoNamedNode OpMapsTo = new(Ontopilot + "mapsTo");
    public static readonly OntoNamedNode OpOrigin = new(Ontopilot + "origin");
}

// ----------------------------------------------------------------------
// DTOs
// ----------------------------------------------------------------------

/// <summary>Payload for <see cref="SkosManager.CreateScheme"/>.</summary>
public sealed record SkosSchemeData(
    string? Iri = null,
    string Title = "",
    string DefaultLanguage = "zh-CN",
    string Description = "",
    string Origin = "manual");

/// <summary>One localizable label set for a SKOS concept.</summary>
public sealed record SkosLabel(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("language")] string Language = "");

/// <summary>Payload for <see cref="SkosManager.CreateConcept"/>.</summary>
public sealed record SkosConceptData(
    string? Iri = null,
    string SchemeIri = "",
    string PrefLabel = "",
    string Language = "en",
    IReadOnlyList<SkosLabel>? AltLabels = null,
    IReadOnlyList<SkosLabel>? HiddenLabels = null,
    IReadOnlyList<string>? Broader = null,
    IReadOnlyList<string>? Related = null,
    string Description = "",
    string Notation = "",
    string Status = "active",
    string Origin = "manual",
    string? MappedEntityIri = null)
{
    internal IReadOnlyList<SkosLabel> EffectiveAltLabels => AltLabels ?? Array.Empty<SkosLabel>();
    internal IReadOnlyList<SkosLabel> EffectiveHiddenLabels => HiddenLabels ?? Array.Empty<SkosLabel>();
    internal IReadOnlyList<string> EffectiveBroader => Broader ?? Array.Empty<string>();
    internal IReadOnlyList<string> EffectiveRelated => Related ?? Array.Empty<string>();
}

/// <summary>One concept as it appears in <see cref="SkosView"/>.</summary>
public sealed record SkosConceptView(
    [property: JsonPropertyName("iri")] string Iri,
    [property: JsonPropertyName("scheme_iri")] string SchemeIri,
    [property: JsonPropertyName("pref_labels")] IReadOnlyList<SkosLabel> PrefLabels,
    [property: JsonPropertyName("alt_labels")] IReadOnlyList<SkosLabel> AltLabels,
    [property: JsonPropertyName("hidden_labels")] IReadOnlyList<SkosLabel> HiddenLabels,
    [property: JsonPropertyName("display_label")] string DisplayLabel,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("notation")] string Notation,
    [property: JsonPropertyName("broader")] IReadOnlyList<string> Broader,
    [property: JsonPropertyName("related")] IReadOnlyList<string> Related,
    [property: JsonPropertyName("broader_labels")] IReadOnlyList<string> BroaderLabels,
    [property: JsonPropertyName("related_labels")] IReadOnlyList<string> RelatedLabels,
    [property: JsonPropertyName("mapped_entity_iri")] string? MappedEntityIri,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("modified_at")] string ModifiedAt);

/// <summary>One scheme as it appears in <see cref="SkosView"/>.</summary>
public sealed record SkosSchemeView(
    [property: JsonPropertyName("iri")] string Iri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("titles")] IReadOnlyList<SkosLabel> Titles,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("descriptions")] IReadOnlyList<SkosLabel> Descriptions,
    [property: JsonPropertyName("default_language")] string DefaultLanguage,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("modified_at")] string ModifiedAt,
    [property: JsonPropertyName("concept_count")] int ConceptCount);

/// <summary>Curated view of the SKOS graph for one knowledge system.</summary>
public sealed record SkosView(
    [property: JsonPropertyName("schemes")] IReadOnlyList<SkosSchemeView> Schemes,
    [property: JsonPropertyName("concepts")] IReadOnlyList<SkosConceptView> Concepts,
    [property: JsonPropertyName("stats")] SkosStats Stats);

/// <summary>Roll-up counts surfaced alongside a <see cref="SkosView"/>.</summary>
public sealed record SkosStats(
    [property: JsonPropertyName("scheme_count")] int SchemeCount,
    [property: JsonPropertyName("concept_count")] int ConceptCount,
    [property: JsonPropertyName("label_count")] int LabelCount,
    [property: JsonPropertyName("mapped_count")] int MappedCount,
    [property: JsonPropertyName("unmapped_count")] int UnmappedCount);

/// <summary>One match in <see cref="SkosManager.Resolve"/>.</summary>
public sealed record SkosMatch(
    [property: JsonPropertyName("concept")] SkosConceptView Concept,
    [property: JsonPropertyName("matched_label")] SkosLabel MatchedLabel,
    [property: JsonPropertyName("match_type")] string MatchType,
    [property: JsonPropertyName("score")] double Score);

/// <summary>One page from <see cref="SkosManager.ListConcepts"/>.</summary>
public sealed record SkosConceptPage(
    [property: JsonPropertyName("items")] IReadOnlyList<SkosConceptView> Items,
    [property: JsonPropertyName("total")] int Total);

/// <summary>Thrown when SKOS payload validation fails (mirrors the Python
/// <c>VocabularyValidationError</c>).</summary>
public sealed class SkosValidationException : Exception
{
    public SkosValidationException(string message) : base(message) { }
    public SkosValidationException(string message, Exception inner) : base(message, inner) { }
}

// ----------------------------------------------------------------------
// SkosManager
// ----------------------------------------------------------------------

/// <summary>
/// RDF-native controlled vocabularies. One knowledge system owns a third
/// named graph (<see cref="KsContext.VocabularyGraph"/>) holding SKOS
/// ConceptSchemes + Concepts. Mirrors <c>backend/app/ontology/skos.py</c>.
/// </summary>
public sealed class SkosManager
{
    private readonly StoreWrapper _store;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public SkosManager(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string NormalizeLabel(string? value) =>
        WhitespaceRun.Replace((value ?? "").Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant().Trim(), " ");

    private static string NowIso() => DateTimeOffset.UtcNow.ToString("o");

    private static string Local(string iri) =>
        iri.Contains('#') ? iri[(iri.LastIndexOf('#') + 1)..] : iri.TrimEnd('/').Split('/')[^1];

    private static OntoLiteral MakeLiteral(string value, string language)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0)
            throw new SkosValidationException("Label values cannot be empty");
        var lang = (language ?? "").Trim();
        return lang.Length == 0 ? new OntoLiteral(cleaned) : new OntoLiteral(cleaned, Language: lang);
    }

    private static SkosLabel MakeLabel(string value, string language)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0)
            throw new SkosValidationException("Label values cannot be empty");
        return new SkosLabel(cleaned, (language ?? "").Trim());
    }

    private IReadOnlyDictionary<string, List<(OntoNamedNode Predicate, object Object)>> SubjectIndex(KsContext ks)
    {
        var out_ = new Dictionary<string, List<(OntoNamedNode, object)>>(StringComparer.Ordinal);
        var g = new OntoNamedNode(ks.VocabularyGraph);
        foreach (var q in _store.Match(graph: g))
        {
            if (q.Subject is OntoNamedNode n)
            {
                if (!out_.TryGetValue(n.Value, out var list))
                {
                    list = new List<(OntoNamedNode, object)>();
                    out_[n.Value] = list;
                }
                list.Add((q.Predicate, q.Object));
            }
        }
        return out_;
    }

    // ------------------------------------------------------------------
    // BuildView
    // ------------------------------------------------------------------

    /// <summary>Read the vocabulary graph into a curated view.</summary>
    public SkosView BuildView(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var subjects = SubjectIndex(ks);

        var schemeIris = new HashSet<string>(StringComparer.Ordinal);
        var conceptIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (iri, pairs) in subjects)
        {
            foreach (var (pred, obj) in pairs)
            {
                if (pred.Value == Vocabulary.RdfType.Value)
                {
                    if (obj is OntoNamedNode t && t.Value == SkosVocab.ConceptScheme.Value) schemeIris.Add(iri);
                    else if (obj is OntoNamedNode t2 && t2.Value == SkosVocab.Concept.Value) conceptIris.Add(iri);
                }
            }
        }

        var schemes = new List<SkosSchemeView>();
        foreach (var iri in schemeIris)
        {
            var pairs = subjects[iri];
            var titles = pairs.Where(p => p.Predicate.Value == SkosVocab.DcTitle.Value
                && p.Object is OntoLiteral)
                .Select(p => ToLabel((OntoLiteral)p.Object)).ToList();
            var descriptions = pairs.Where(p => p.Predicate.Value == SkosVocab.DcDescription.Value
                && p.Object is OntoLiteral)
                .Select(p => ToLabel((OntoLiteral)p.Object)).ToList();
            schemes.Add(new SkosSchemeView(
                Iri: iri,
                Title: titles.Count > 0 ? titles[0].Value : Local(iri),
                Titles: titles,
                Description: descriptions.Count > 0 ? descriptions[0].Value : "",
                Descriptions: descriptions,
                DefaultLanguage: FirstLiteral(pairs, SkosVocab.OpDefaultLanguage.Value, "zh-CN"),
                Origin: FirstLiteral(pairs, SkosVocab.OpOrigin.Value, "manual"),
                CreatedAt: FirstLiteral(pairs, SkosVocab.DcCreated.Value),
                ModifiedAt: FirstLiteral(pairs, SkosVocab.DcModified.Value),
                ConceptCount: 0));
        }

        var concepts = new List<SkosConceptView>();
        foreach (var iri in conceptIris)
        {
            var pairs = subjects[iri];
            var pref = pairs.Where(p => p.Predicate.Value == SkosVocab.PrefLabel.Value
                && p.Object is OntoLiteral)
                .Select(p => ToLabel((OntoLiteral)p.Object)).ToList();
            var alt = pairs.Where(p => p.Predicate.Value == SkosVocab.AltLabel.Value
                && p.Object is OntoLiteral)
                .Select(p => ToLabel((OntoLiteral)p.Object)).ToList();
            var hidden = pairs.Where(p => p.Predicate.Value == SkosVocab.HiddenLabel.Value
                && p.Object is OntoLiteral)
                .Select(p => ToLabel((OntoLiteral)p.Object)).ToList();
            var schemesFor = pairs.Where(p => p.Predicate.Value == SkosVocab.InScheme.Value
                && p.Object is OntoNamedNode)
                .Select(p => ((OntoNamedNode)p.Object).Value).ToList();
            var broader = pairs.Where(p => p.Predicate.Value == SkosVocab.Broader.Value
                && p.Object is OntoNamedNode)
                .Select(p => ((OntoNamedNode)p.Object).Value).ToList();
            var related = pairs.Where(p => p.Predicate.Value == SkosVocab.Related.Value
                && p.Object is OntoNamedNode)
                .Select(p => ((OntoNamedNode)p.Object).Value).ToList();
            var mapped = pairs.Where(p => p.Predicate.Value == SkosVocab.OpMapsTo.Value
                && p.Object is OntoNamedNode)
                .Select(p => ((OntoNamedNode)p.Object).Value).ToList();
            concepts.Add(new SkosConceptView(
                Iri: iri,
                SchemeIri: schemesFor.Count > 0 ? schemesFor[0] : "",
                PrefLabels: pref,
                AltLabels: alt,
                HiddenLabels: hidden,
                DisplayLabel: pref.Count > 0 ? pref[0].Value : Local(iri),
                Description: FirstLiteral(pairs, SkosVocab.Definition.Value),
                Notation: FirstLiteral(pairs, SkosVocab.Notation.Value),
                Broader: broader,
                Related: related,
                BroaderLabels: new List<string>(),
                RelatedLabels: new List<string>(),
                MappedEntityIri: mapped.Count > 0 ? mapped[0] : null,
                Status: FirstLiteral(pairs, SkosVocab.OpStatus.Value, "active"),
                Origin: FirstLiteral(pairs, SkosVocab.OpOrigin.Value, "manual"),
                CreatedAt: FirstLiteral(pairs, SkosVocab.DcCreated.Value),
                ModifiedAt: FirstLiteral(pairs, SkosVocab.DcModified.Value)));
        }

        var byIri = concepts.ToDictionary(c => c.Iri, c => c, StringComparer.Ordinal);
        var withLabels = concepts.Select(c =>
        {
            var broaderLabels = c.Broader
                .Select(b => byIri.TryGetValue(b, out var x) ? x.DisplayLabel : Local(b))
                .ToList();
            var relatedLabels = c.Related
                .Select(r => byIri.TryGetValue(r, out var x) ? x.DisplayLabel : Local(r))
                .ToList();
            return c with { BroaderLabels = broaderLabels, RelatedLabels = relatedLabels };
        }).ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in withLabels)
        {
            counts[c.SchemeIri] = counts.GetValueOrDefault(c.SchemeIri, 0) + 1;
        }
        var finalSchemes = schemes.Select(s => s with { ConceptCount = counts.GetValueOrDefault(s.Iri, 0) }).ToList();

        finalSchemes.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        withLabels.Sort((a, b) => string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));

        var labelCount = withLabels.Sum(c => c.PrefLabels.Count + c.AltLabels.Count + c.HiddenLabels.Count);
        var mappedCount = withLabels.Count(c => !string.IsNullOrEmpty(c.MappedEntityIri));
        var stats = new SkosStats(
            SchemeCount: finalSchemes.Count,
            ConceptCount: withLabels.Count,
            LabelCount: labelCount,
            MappedCount: mappedCount,
            UnmappedCount: withLabels.Count - mappedCount);
        return new SkosView(finalSchemes, withLabels, stats);
    }

    private static SkosLabel ToLabel(OntoLiteral lit) =>
        new(lit.Value, lit.Language ?? "");

    private static string FirstLiteral(IEnumerable<(OntoNamedNode Predicate, object Object)> pairs,
        string predicateIri, string fallback = "")
    {
        foreach (var (p, o) in pairs)
        {
            if (p.Value == predicateIri && o is OntoLiteral lit)
                return lit.Value;
        }
        return fallback;
    }

    // ------------------------------------------------------------------
    // Scheme CRUD
    // ------------------------------------------------------------------

    /// <summary>Create a new ConceptScheme in the vocabulary graph.</summary>
    public string CreateScheme(KsContext ks, SkosSchemeData data)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(data);

        var title = (data.Title ?? "").Trim();
        if (title.Length == 0)
            throw new SkosValidationException("Vocabulary title is required");
        var language = (data.DefaultLanguage ?? "").Trim();
        if (language.Length == 0) language = "zh-CN";
        var description = (data.Description ?? "").Trim();
        var origin = (data.Origin ?? "").Trim();
        if (origin.Length == 0) origin = "manual";

        var iri = data.Iri is { Length: > 0 } explicitIri
            ? explicitIri
            : $"{ks.VocabularyGraph}#scheme-{Guid.NewGuid().ToString("N")[..12]}";
        if (GetScheme(ks, iri) is not null)
            throw new SkosValidationException("A vocabulary with this IRI already exists");

        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var node = new OntoNamedNode(iri);
        var now = NowIso();
        var quads = new List<OntoQuad>
        {
            new(node, Vocabulary.RdfType, SkosVocab.ConceptScheme, graph),
            new(node, SkosVocab.DcTitle, MakeLiteral(title, language), graph),
            new(node, SkosVocab.OpDefaultLanguage, new OntoLiteral(language), graph),
            new(node, SkosVocab.OpOrigin, new OntoLiteral(origin), graph),
            new(node, SkosVocab.DcCreated, new OntoLiteral(now), graph),
            new(node, SkosVocab.DcModified, new OntoLiteral(now), graph),
        };
        if (description.Length > 0)
        {
            quads.Add(new(node, SkosVocab.DcDescription, MakeLiteral(description, language), graph));
        }
        _store.AddQuads(graph, quads);
        return iri;
    }

    /// <summary>Update an existing scheme (replaces the scheme-predicate set).</summary>
    public string UpdateScheme(KsContext ks, string iri, SkosSchemeData data)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentNullException.ThrowIfNull(data);

        var existing = GetScheme(ks, iri)
            ?? throw new SkosValidationException("Vocabulary not found");

        var title = (data.Title ?? existing.Title).Trim();
        if (title.Length == 0)
            throw new SkosValidationException("Vocabulary title is required");
        var language = (data.DefaultLanguage ?? existing.DefaultLanguage).Trim();
        if (language.Length == 0) language = "zh-CN";
        var description = (data.Description ?? existing.Description).Trim();
        var origin = (data.Origin ?? existing.Origin).Trim();
        if (origin.Length == 0) origin = "manual";

        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var node = new OntoNamedNode(iri);
        RemovePredicates(ks, node, SchemePredicates);
        var quads = new List<OntoQuad>
        {
            new(node, SkosVocab.DcTitle, MakeLiteral(title, language), graph),
            new(node, SkosVocab.OpDefaultLanguage, new OntoLiteral(language), graph),
            new(node, SkosVocab.OpOrigin, new OntoLiteral(origin), graph),
            new(node, SkosVocab.DcModified, new OntoLiteral(NowIso()), graph),
        };
        if (description.Length > 0)
            quads.Add(new(node, SkosVocab.DcDescription, MakeLiteral(description, language), graph));
        _store.AddQuads(graph, quads);
        return iri;
    }

    /// <summary>Look up a single scheme by IRI (returns null if not found).</summary>
    public SkosSchemeView? GetScheme(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        return BuildView(ks).Schemes.FirstOrDefault(s => s.Iri == iri);
    }

    /// <summary>Look up a single concept by IRI (returns null if not found).</summary>
    public SkosConceptView? GetConcept(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        return BuildView(ks).Concepts.FirstOrDefault(c => c.Iri == iri);
    }

    /// <summary>Delete the scheme + every concept that referenced it.</summary>
    public int DeleteScheme(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var view = BuildView(ks);
        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var removed = 0;
        foreach (var c in view.Concepts.Where(c => c.SchemeIri == iri))
        {
            removed += RemoveEntity(graph, c.Iri);
        }
        removed += RemoveEntity(graph, iri);
        return removed;
    }

    // ------------------------------------------------------------------
    // Concept CRUD
    // ------------------------------------------------------------------

    private static readonly HashSet<OntoNamedNode> SchemePredicates = new()
    {
        SkosVocab.DcTitle, SkosVocab.DcDescription, SkosVocab.DcModified,
        SkosVocab.OpDefaultLanguage, SkosVocab.OpOrigin,
    };

    private static readonly HashSet<OntoNamedNode> ConceptPredicates = new()
    {
        SkosVocab.InScheme, SkosVocab.PrefLabel, SkosVocab.AltLabel, SkosVocab.HiddenLabel,
        SkosVocab.Broader, SkosVocab.Related, SkosVocab.Notation, SkosVocab.Definition,
        SkosVocab.DcModified, SkosVocab.OpStatus, SkosVocab.OpMapsTo, SkosVocab.OpOrigin,
    };

    /// <summary>Create a new Concept (with full validation).</summary>
    public string CreateConcept(KsContext ks, string schemeIri, SkosConceptData data)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(schemeIri);
        ArgumentNullException.ThrowIfNull(data);

        var withScheme = data with { SchemeIri = schemeIri };
        var cleaned = ValidateConcept(ks, withScheme, excludeIri: null);

        var iri = data.Iri is { Length: > 0 } explicitIri
            ? explicitIri
            : $"{ks.VocabularyGraph}#concept-{Guid.NewGuid().ToString("N")[..16]}";
        if (GetConcept(ks, iri) is not null)
            throw new SkosValidationException("A concept with this IRI already exists");

        var quads = ConceptTriples(iri, cleaned, createdAt: NowIso(), graph: new OntoNamedNode(ks.VocabularyGraph));
        _store.AddQuads(new OntoNamedNode(ks.VocabularyGraph), quads);
        return iri;
    }

    /// <summary>Update an existing concept (replaces the concept-predicate set).</summary>
    public string UpdateConcept(KsContext ks, string iri, SkosConceptData data)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        ArgumentNullException.ThrowIfNull(data);

        var existing = GetConcept(ks, iri)
            ?? throw new SkosValidationException("Concept not found");

        var source = data with
        {
            SchemeIri = data.SchemeIri.Length > 0 ? data.SchemeIri : existing.SchemeIri,
            Origin = data.Origin.Length > 0 ? data.Origin : existing.Origin,
        };
        var cleaned = ValidateConcept(ks, source, excludeIri: iri);
        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var node = new OntoNamedNode(iri);
        RemovePredicates(ks, node, ConceptPredicates);
        // Also drop any inbound `skos:related -> iri` triples.
        var inbound = _store.Match(predicateIri: SkosVocab.Related.Value,
            objectIri: iri, graphIri: ks.VocabularyGraph);
        if (inbound.Count > 0) _store.RemoveQuads(graph, inbound);
        var quads = ConceptTriples(iri, cleaned, createdAt: existing.CreatedAt.Length > 0 ? existing.CreatedAt : null, graph: graph);
        _store.AddQuads(graph, quads);
        return iri;
    }

    /// <summary>Delete the concept + every triple that mentions its IRI.</summary>
    public int DeleteConcept(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var inbound = _store.Match(predicateIri: SkosVocab.Related.Value,
            objectIri: iri, graphIri: ks.VocabularyGraph);
        if (inbound.Count > 0) _store.RemoveQuads(graph, inbound);
        return RemoveEntity(graph, iri);
    }

    private void RemovePredicates(KsContext ks, OntoNamedNode subject, HashSet<OntoNamedNode> predicates)
    {
        var graph = new OntoNamedNode(ks.VocabularyGraph);
        foreach (var pred in predicates)
        {
            var existing = _store.Match(subjectIri: subject.Value, predicateIri: pred.Value,
                graphIri: ks.VocabularyGraph);
            if (existing.Count > 0) _store.RemoveQuads(graph, existing);
        }
    }

    private int RemoveEntity(OntoNamedNode graph, string iri)
    {
        var outgoing = _store.Match(subjectIri: iri, graphIri: graph.Value);
        if (outgoing.Count > 0)
        {
            _store.RemoveQuads(graph, outgoing);
        }
        return outgoing.Count;
    }

    private static List<OntoQuad> ConceptTriples(string iri, SkosConceptData data, string? createdAt, OntoNamedNode graph)
    {
        var node = new OntoNamedNode(iri);
        var now = NowIso();
        var triples = new List<OntoQuad>
        {
            new(node, Vocabulary.RdfType, SkosVocab.Concept, graph),
            new(node, SkosVocab.InScheme, new OntoNamedNode(data.SchemeIri), graph),
            new(node, SkosVocab.OpStatus, new OntoLiteral(data.Status), graph),
            new(node, SkosVocab.OpOrigin, new OntoLiteral(data.Origin), graph),
            new(node, SkosVocab.DcModified, new OntoLiteral(now), graph),
        };
        if (createdAt is { Length: > 0 })
            triples.Add(new(node, SkosVocab.DcCreated, new OntoLiteral(createdAt), graph));
        foreach (var l in new[] { data.PrefLabel })
        {
            triples.Add(new(node, SkosVocab.PrefLabel, MakeLiteral(l, data.Language), graph));
        }
        foreach (var l in data.EffectiveAltLabels)
        {
            triples.Add(new(node, SkosVocab.AltLabel, MakeLiteral(l.Value, l.Language), graph));
        }
        foreach (var l in data.EffectiveHiddenLabels)
        {
            triples.Add(new(node, SkosVocab.HiddenLabel, MakeLiteral(l.Value, l.Language), graph));
        }
        if (data.Description.Length > 0)
        {
            triples.Add(new(node, SkosVocab.Definition, MakeLiteral(data.Description, data.Language), graph));
        }
        if (data.Notation.Length > 0)
        {
            triples.Add(new(node, SkosVocab.Notation, new OntoLiteral(data.Notation), graph));
        }
        foreach (var parent in data.EffectiveBroader)
        {
            triples.Add(new(node, SkosVocab.Broader, new OntoNamedNode(parent), graph));
        }
        foreach (var related in data.EffectiveRelated)
        {
            triples.Add(new(node, SkosVocab.Related, new OntoNamedNode(related), graph));
            triples.Add(new(new OntoNamedNode(related), SkosVocab.Related, node, graph));
        }
        if (!string.IsNullOrEmpty(data.MappedEntityIri))
        {
            triples.Add(new(node, SkosVocab.OpMapsTo, new OntoNamedNode(data.MappedEntityIri), graph));
        }
        return triples;
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private SkosConceptData ValidateConcept(KsContext ks, SkosConceptData data, string? excludeIri)
    {
        var view = BuildView(ks);
        var schemeIri = data.SchemeIri;
        if (!view.Schemes.Any(s => s.Iri == schemeIri))
            throw new SkosValidationException("Vocabulary scheme not found");

        var pref = Labels(new[] { new SkosLabel(data.PrefLabel, data.Language) }, required: true);
        var alt = Labels(data.EffectiveAltLabels, required: false);
        var hidden = Labels(data.EffectiveHiddenLabels, required: false);

        var prefLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in pref)
        {
            if (!prefLanguages.Add(l.Language))
                throw new SkosValidationException("A concept may have only one preferred label per language");
        }
        var incoming = new HashSet<(string Norm, string Lang)>();
        foreach (var l in pref.Concat(alt).Concat(hidden))
        {
            incoming.Add((NormalizeLabel(l.Value), l.Language.ToLowerInvariant()));
        }
        if (incoming.Count != pref.Count + alt.Count + hidden.Count)
            throw new SkosValidationException("The same label cannot be preferred, alternative, or hidden twice");
        foreach (var concept in view.Concepts)
        {
            if (concept.Iri == excludeIri || concept.SchemeIri != schemeIri) continue;
            var existing = new HashSet<(string Norm, string Lang)>();
            foreach (var l in concept.PrefLabels.Concat(concept.AltLabels).Concat(concept.HiddenLabels))
            {
                existing.Add((NormalizeLabel(l.Value), l.Language.ToLowerInvariant()));
            }
            var overlap = new HashSet<(string Norm, string Lang)>(incoming);
            overlap.IntersectWith(existing);
            if (overlap.Count > 0)
            {
                var dup = overlap.First().Norm;
                throw new SkosValidationException(
                    $"Label \"{dup}\" is already used by concept \"{concept.DisplayLabel}\"");
            }
        }

        var broader = data.EffectiveBroader.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var related = data.EffectiveRelated.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var byIri = view.Concepts.ToDictionary(c => c.Iri, c => c, StringComparer.Ordinal);
        foreach (var rel in broader.Concat(related))
        {
            if (!byIri.TryGetValue(rel, out var target) || target.SchemeIri != schemeIri)
                throw new SkosValidationException("Broader and related concepts must exist in the same vocabulary");
            if (excludeIri is { Length: > 0 } current && rel == current)
                throw new SkosValidationException("A concept cannot relate to itself");
        }
        if (excludeIri is { Length: > 0 } currentIri)
        {
            var adjacency = view.Concepts.ToDictionary(
                c => c.Iri,
                c => (ISet<string>)new HashSet<string>(c.Broader, StringComparer.Ordinal),
                StringComparer.Ordinal);
            adjacency[currentIri] = new HashSet<string>(broader, StringComparer.Ordinal);

            bool Reaches(string start, string target)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<string>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n == target) return true;
                    if (!seen.Add(n)) continue;
                    if (adjacency.TryGetValue(n, out var ss))
                    {
                        foreach (var s in ss) stack.Push(s);
                    }
                }
                return false;
            }
            foreach (var p in broader)
            {
                if (Reaches(p, currentIri))
                    throw new SkosValidationException("Broader relations cannot form a cycle");
            }
        }

        var status = (data.Status ?? "active").Trim();
        if (status.Length == 0) status = "active";
        if (status is not ("active" or "deprecated"))
            throw new SkosValidationException("Status must be active or deprecated");
        var mappedEntityIri = string.IsNullOrWhiteSpace(data.MappedEntityIri) ? null : data.MappedEntityIri.Trim();
        if (mappedEntityIri is { Length: > 0 } && !mappedEntityIri.StartsWith("http://", StringComparison.Ordinal)
            && !mappedEntityIri.StartsWith("https://", StringComparison.Ordinal)
            && !mappedEntityIri.StartsWith("urn:", StringComparison.Ordinal))
        {
            throw new SkosValidationException("Ontology mapping must be an absolute IRI");
        }
        var origin = (data.Origin ?? "manual").Trim();
        if (origin.Length == 0) origin = "manual";
        if (origin is not ("manual" or "extraction" or "agent"))
            throw new SkosValidationException("Origin must be manual, extraction, or agent");

        return data with
        {
            Status = status,
            Origin = origin,
            MappedEntityIri = mappedEntityIri,
        };
    }

    private static List<SkosLabel> Labels(IEnumerable<SkosLabel> values, bool required)
    {
        var out_ = new List<SkosLabel>();
        var seen = new HashSet<(string Norm, string Lang)>();
        foreach (var item in values)
        {
            var v = (item.Value ?? "").Trim();
            var l = (item.Language ?? "").Trim();
            if (v.Length == 0) continue;
            MakeLabel(v, l); // throws on invalid lang
            var key = (NormalizeLabel(v), l.ToLowerInvariant());
            if (seen.Add(key))
                out_.add(new SkosLabel(v, l));
        }
        if (required && out_.Count == 0)
            throw new SkosValidationException("At least one preferred label is required");
        return out_;
    }

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------

    /// <summary>
    /// Page through concepts with optional filters: <c>scheme_iri</c>,
    /// <c>status</c> (active|deprecated), <c>mapping</c> (mapped|standalone),
    /// <c>origin</c> (manual|extraction|agent), and a date range on
    /// (modified OR created). Mirrors Python <c>list_concepts</c>.
    /// </summary>
    public SkosConceptPage ListConcepts(
        KsContext ks,
        string? SchemeIri = null,
        string? Status = null,
        string? Mapping = null,
        string? Origin = null,
        string? StartDate = null,
        string? EndDate = null,
        string? Q = null,
        int Limit = 100,
        int Offset = 0)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var concepts = BuildView(ks).Concepts;
        if (!string.IsNullOrWhiteSpace(SchemeIri))
            concepts = concepts.Where(c => c.SchemeIri == SchemeIri).ToList();
        if (!string.IsNullOrWhiteSpace(Status))
            concepts = concepts.Where(c => c.Status == Status).ToList();
        if (Mapping == "mapped")
            concepts = concepts.Where(c => !string.IsNullOrEmpty(c.MappedEntityIri)).ToList();
        else if (Mapping == "standalone")
            concepts = concepts.Where(c => string.IsNullOrEmpty(c.MappedEntityIri)).ToList();
        if (!string.IsNullOrWhiteSpace(Origin))
            concepts = concepts.Where(c => c.Origin == Origin).ToList();
        if (!string.IsNullOrWhiteSpace(StartDate) || !string.IsNullOrWhiteSpace(EndDate))
        {
            concepts = concepts.Where(c =>
            {
                var stamp = (c.ModifiedAt.Length > 0 ? c.ModifiedAt : c.CreatedAt);
                var date = stamp.Length >= 10 ? stamp[..10] : "";
                if (!string.IsNullOrEmpty(StartDate) && date.Length > 0 && string.Compare(date, StartDate, StringComparison.Ordinal) < 0) return false;
                if (!string.IsNullOrEmpty(EndDate) && date.Length > 0 && string.Compare(date, EndDate, StringComparison.Ordinal) > 0) return false;
                return true;
            }).ToList();
        }
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = NormalizeLabel(Q);
            concepts = concepts.Where(c =>
            {
                var hay = NormalizeLabel(string.Join(' ', new[]
                {
                    c.Description, c.Notation,
                }.Concat(c.PrefLabels.Select(l => l.Value))
                 .Concat(c.AltLabels.Select(l => l.Value))
                 .Concat(c.HiddenLabels.Select(l => l.Value))));
                return term.Length > 0 && hay.Contains(term);
            }).ToList();
        }
        var total = concepts.Count;
        var items = concepts.Skip(Offset).Take(Limit).ToList();
        return new SkosConceptPage(items, total);
    }

    /// <summary>
    /// Resolve free text to concepts. Matches against pref / alt / hidden
    /// labels with scores 1.0 / 0.98 / 0.95. Optional <paramref name="Language"/>
    /// filter limits the labels considered. Mirrors Python <c>resolve</c>.
    /// </summary>
    public (IReadOnlyList<SkosMatch> Items, int Total) Resolve(
        KsContext ks,
        string text,
        string? Language = null,
        int Limit = 10)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var query = NormalizeLabel(text);
        var matches = new List<SkosMatch>();
        foreach (var c in BuildView(ks).Concepts)
        {
            if (c.Status != "active") continue;
            foreach (var (kind, labels, exactScore) in new[]
            {
                ("preferred", c.PrefLabels, 1.0),
                ("alternative", c.AltLabels, 0.98),
                ("hidden", c.HiddenLabels, 0.95),
            })
            {
                foreach (var l in labels)
                {
                    if (!string.IsNullOrWhiteSpace(Language)
                        && !string.IsNullOrEmpty(l.Language)
                        && !string.Equals(Language, l.Language, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var normalized = NormalizeLabel(l.Value);
                    double score;
                    if (query == normalized) score = exactScore;
                    else if (query.Length > 0 && normalized.Contains(query)) score = 0.72;
                    else score = 0.0;
                    if (score > 0)
                        matches.Add(new SkosMatch(c, l, kind, score));
                }
            }
        }
        matches.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Concept.DisplayLabel, b.Concept.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<SkosMatch>();
        foreach (var m in matches)
        {
            if (seen.Add(m.Concept.Iri))
                unique.Add(m);
        }
        return (unique.Take(Limit).ToList(), unique.Count);
    }

    // ------------------------------------------------------------------
    // Aliases (entity-resolution helpers)
    // ------------------------------------------------------------------

    /// <summary>
    /// Map a normalized label string to its target ontology IRI when exactly
    /// one mapped concept in the vocabulary advertises that label.
    /// </summary>
    public IReadOnlyDictionary<string, string> MappedAliases(KsContext ks)
    {
        ArgumentNullException.ThrowIfNull(ks);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var c in BuildView(ks).Concepts)
        {
            if (c.Status != "active" || string.IsNullOrEmpty(c.MappedEntityIri)) continue;
            foreach (var l in c.PrefLabels.Concat(c.AltLabels).Concat(c.HiddenLabels))
            {
                if (!candidates.TryGetValue(NormalizeLabel(l.Value), out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    candidates[NormalizeLabel(l.Value)] = set;
                }
                set.Add(c.MappedEntityIri!);
            }
        }
        var out_ = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, targets) in candidates)
        {
            if (targets.Count == 1)
                out_[label] = targets.First();
        }
        return out_;
    }
}

// ----------------------------------------------------------------------
// Extension helpers
// ----------------------------------------------------------------------

internal static class SkosLabelListExtensions
{
    /// <summary>
    /// Helper used by SkosManager.Labels to deduplicate labels preserving
    /// the caller's order. Mirrors Python's <c>list.append</c> + seen-set
    /// pattern.
    /// </summary>
    public static void add<T>(this List<T> list, T item)
    {
        list.Add(item);
    }
}