using System.Text;
using System.Text.RegularExpressions;
using ISEStudio.Application.Vocabulary;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Ontology;

// ----------------------------------------------------------------------
// SKOS namespace constants
// ----------------------------------------------------------------------

/// <summary>SKOS, DCTERMS, and ISEStudio vocabulary constants for the
/// vocabulary layer.</summary>
public static class SkosVocab
{
    public const string Skos = "http://www.w3.org/2004/02/skos/core#";
    public const string Dcterms = "http://purl.org/dc/terms/";

    /// <summary>
    /// ISEStudio vocabulary namespace prefix (the <c>op:</c> shorthand in
    /// Turtle). Settable at startup via <see cref="Configure"/> so the
    /// runtime prefix tracks <c>ISEStudio:VocabNamespace</c> without
    /// recompiling. Must end with <c>#</c>.
    /// </summary>
    public static string IseStudio { get; private set; } = "http://goodcrew.local/vocab#";

    /// <summary>
    /// Set the ISEStudio vocabulary prefix from configuration. Call once
    /// during host startup (<c>Program.cs</c>). Triggers a rebuild of the
    /// cached <c>op:*</c> predicate IRIs on next access so any test or
    /// hot reload that changes the prefix at runtime sees the new values.
    /// </summary>
    public static void Configure(string vocabNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(vocabNamespace);
        if (!vocabNamespace.EndsWith('#'))
            throw new ArgumentException(
                "VocabNamespace must end with '#' (SHACL @prefix + IseStudio + 'predicate' concatenation).",
                nameof(vocabNamespace));
        IseStudio = vocabNamespace;
        _opDefaultLanguage = null;
        _opStatus = null;
        _opMapsTo = null;
        _opOrigin = null;
    }

    public const string ConceptScheme = Skos + "ConceptScheme";
    public const string Concept = Skos + "Concept";
    public const string InScheme = Skos + "inScheme";
    public const string PrefLabel = Skos + "prefLabel";
    public const string AltLabel = Skos + "altLabel";
    public const string HiddenLabel = Skos + "hiddenLabel";
    public const string Broader = Skos + "broader";
    public const string Related = Skos + "related";
    public const string Notation = Skos + "notation";
    public const string Definition = Skos + "definition";

    public const string DcTitle = Dcterms + "title";
    public const string DcDescription = Dcterms + "description";
    public const string DcCreated = Dcterms + "created";
    public const string DcModified = Dcterms + "modified";

    // op:* predicate IRIs depend on the configurable IseStudio prefix, so
    // they are lazy + invalidated by Configure(). Read via the property
    // accessors below so any code path that needs them always sees the
    // current prefix.
    private static string? _opDefaultLanguage;
    private static string? _opStatus;
    private static string? _opMapsTo;
    private static string? _opOrigin;

    public static string OpDefaultLanguage =>
        _opDefaultLanguage ??= IseStudio + "defaultLanguage";
    public static string OpStatus =>
        _opStatus ??= IseStudio + "status";
    public static string OpMapsTo =>
        _opMapsTo ??= IseStudio + "mapsTo";
    public static string OpOrigin =>
        _opOrigin ??= IseStudio + "origin";
}

// ----------------------------------------------------------------------
// SkosManager
// ----------------------------------------------------------------------

/// <summary>Thrown when SKOS payload validation fails (mirrors the Python
/// <c>VocabularyValidationError</c>).</summary>
public sealed class SkosValidationException : Exception
{
    public SkosValidationException(string message) : base(message) { }
    public SkosValidationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// RDF-native controlled vocabularies. One knowledge system owns a third
/// named graph (<see cref="KsContext.VocabularyGraph"/>) holding SKOS
/// ConceptSchemes + Concepts. Mirrors <c>backend/app/ontology/skos.py</c>.
/// </summary>
public sealed class SkosManager
{
    private readonly IVocabularyGraphStore _store;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public SkosManager(IRdfStatementRepository statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        _store = new PostgresVocabularyGraphStore(statements);
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

    private static RdfLiteral MakeLiteral(string value, string language)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0)
            throw new SkosValidationException("Label values cannot be empty");
        var lang = (language ?? "").Trim();
        return lang.Length == 0 ? new RdfLiteral(cleaned) : new RdfLiteral(cleaned, Language: lang);
    }

    private static SkosLabel MakeLabel(string value, string language)
    {
        var cleaned = (value ?? "").Trim();
        if (cleaned.Length == 0)
            throw new SkosValidationException("Label values cannot be empty");
        return new SkosLabel(cleaned, (language ?? "").Trim());
    }

    private IReadOnlyDictionary<string, List<(string Predicate, RdfTerm Object)>> SubjectIndex(KsContext ks)
    {
        var out_ = new Dictionary<string, List<(string, RdfTerm)>>(StringComparer.Ordinal);
        foreach (var s in _store.Match(ks, graphIri: ks.VocabularyGraph))
        {
            if (s.Subject is RdfIri n)
            {
                if (!out_.TryGetValue(n.Value, out var list))
                {
                    list = new List<(string, RdfTerm)>();
                    out_[n.Value] = list;
                }
                list.Add((s.PredicateIri, s.Object));
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
                if (pred == Vocabulary.RdfType)
                {
                    if (obj is RdfIri t && t.Value == SkosVocab.ConceptScheme) schemeIris.Add(iri);
                    else if (obj is RdfIri t2 && t2.Value == SkosVocab.Concept) conceptIris.Add(iri);
                }
            }
        }

        var schemes = new List<SkosSchemeView>();
        foreach (var iri in schemeIris)
        {
            var pairs = subjects[iri];
            var titles = pairs.Where(p => p.Predicate == SkosVocab.DcTitle
                && p.Object is RdfLiteral)
                .Select(p => ToLabel((RdfLiteral)p.Object)).ToList();
            var descriptions = pairs.Where(p => p.Predicate == SkosVocab.DcDescription
                && p.Object is RdfLiteral)
                .Select(p => ToLabel((RdfLiteral)p.Object)).ToList();
            schemes.Add(new SkosSchemeView(
                Iri: iri,
                Title: titles.Count > 0 ? titles[0].Value : Local(iri),
                Titles: titles,
                Description: descriptions.Count > 0 ? descriptions[0].Value : "",
                Descriptions: descriptions,
                DefaultLanguage: FirstLiteral(pairs, SkosVocab.OpDefaultLanguage, "zh-CN"),
                Origin: FirstLiteral(pairs, SkosVocab.OpOrigin, "manual"),
                CreatedAt: FirstLiteral(pairs, SkosVocab.DcCreated),
                ModifiedAt: FirstLiteral(pairs, SkosVocab.DcModified),
                ConceptCount: 0));
        }

        var concepts = new List<SkosConceptView>();
        foreach (var iri in conceptIris)
        {
            var pairs = subjects[iri];
            var pref = pairs.Where(p => p.Predicate == SkosVocab.PrefLabel
                && p.Object is RdfLiteral)
                .Select(p => ToLabel((RdfLiteral)p.Object)).ToList();
            var alt = pairs.Where(p => p.Predicate == SkosVocab.AltLabel
                && p.Object is RdfLiteral)
                .Select(p => ToLabel((RdfLiteral)p.Object)).ToList();
            var hidden = pairs.Where(p => p.Predicate == SkosVocab.HiddenLabel
                && p.Object is RdfLiteral)
                .Select(p => ToLabel((RdfLiteral)p.Object)).ToList();
            var schemesFor = pairs.Where(p => p.Predicate == SkosVocab.InScheme
                && p.Object is RdfIri)
                .Select(p => ((RdfIri)p.Object).Value).ToList();
            var broader = pairs.Where(p => p.Predicate == SkosVocab.Broader
                && p.Object is RdfIri)
                .Select(p => ((RdfIri)p.Object).Value).ToList();
            var related = pairs.Where(p => p.Predicate == SkosVocab.Related
                && p.Object is RdfIri)
                .Select(p => ((RdfIri)p.Object).Value).ToList();
            var mapped = pairs.Where(p => p.Predicate == SkosVocab.OpMapsTo
                && p.Object is RdfIri)
                .Select(p => ((RdfIri)p.Object).Value).ToList();
            concepts.Add(new SkosConceptView(
                Iri: iri,
                SchemeIri: schemesFor.Count > 0 ? schemesFor[0] : "",
                PrefLabels: pref,
                AltLabels: alt,
                HiddenLabels: hidden,
                DisplayLabel: pref.Count > 0 ? pref[0].Value : Local(iri),
                Description: FirstLiteral(pairs, SkosVocab.Definition),
                Notation: FirstLiteral(pairs, SkosVocab.Notation),
                Broader: broader,
                Related: related,
                BroaderLabels: new List<string>(),
                RelatedLabels: new List<string>(),
                MappedEntityIri: mapped.Count > 0 ? mapped[0] : null,
                Status: FirstLiteral(pairs, SkosVocab.OpStatus, "active"),
                Origin: FirstLiteral(pairs, SkosVocab.OpOrigin, "manual"),
                CreatedAt: FirstLiteral(pairs, SkosVocab.DcCreated),
                ModifiedAt: FirstLiteral(pairs, SkosVocab.DcModified)));
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

    private static SkosLabel ToLabel(RdfLiteral lit) =>
        new(lit.Value, lit.Language ?? "");

    private static string FirstLiteral(IEnumerable<(string Predicate, RdfTerm Object)> pairs,
        string predicateIri, string fallback = "")
    {
        foreach (var (p, o) in pairs)
        {
            if (p == predicateIri && o is RdfLiteral lit)
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

        var graph = ks.VocabularyGraph;
        var now = NowIso();
        var statements = new List<RdfStatement>
        {
            new(new RdfIri(iri), Vocabulary.RdfType,
                new RdfIri(SkosVocab.ConceptScheme), graph),
            new(new RdfIri(iri), SkosVocab.DcTitle,
                MakeLiteral(title, language), graph),
            new(new RdfIri(iri), SkosVocab.OpDefaultLanguage,
                new RdfLiteral(language), graph),
            new(new RdfIri(iri), SkosVocab.OpOrigin,
                new RdfLiteral(origin), graph),
            new(new RdfIri(iri), SkosVocab.DcCreated,
                new RdfLiteral(now), graph),
            new(new RdfIri(iri), SkosVocab.DcModified,
                new RdfLiteral(now), graph),
        };
        if (description.Length > 0)
        {
            statements.Add(new RdfStatement(
                new RdfIri(iri), SkosVocab.DcDescription,
                MakeLiteral(description, language), graph));
        }
        _store.AddStatements(ks, graph, statements);
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

        var graph = ks.VocabularyGraph;
        RemovePredicates(ks, iri, SchemePredicates);
        var statements = new List<RdfStatement>
        {
            new(new RdfIri(iri), SkosVocab.DcTitle,
                MakeLiteral(title, language), graph),
            new(new RdfIri(iri), SkosVocab.OpDefaultLanguage,
                new RdfLiteral(language), graph),
            new(new RdfIri(iri), SkosVocab.OpOrigin,
                new RdfLiteral(origin), graph),
            new(new RdfIri(iri), SkosVocab.DcModified,
                new RdfLiteral(NowIso()), graph),
        };
        if (description.Length > 0)
            statements.Add(new RdfStatement(
                new RdfIri(iri), SkosVocab.DcDescription,
                MakeLiteral(description, language), graph));
        _store.AddStatements(ks, graph, statements);
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
        var removed = 0;
        foreach (var c in view.Concepts.Where(c => c.SchemeIri == iri))
        {
            removed += RemoveEntity(ks, ks.VocabularyGraph, c.Iri);
        }
        removed += RemoveEntity(ks, ks.VocabularyGraph, iri);
        return removed;
    }

    // ------------------------------------------------------------------
    // Concept CRUD
    // ------------------------------------------------------------------

    private static readonly HashSet<string> SchemePredicates = new()
    {
        SkosVocab.DcTitle, SkosVocab.DcDescription, SkosVocab.DcModified,
        SkosVocab.OpDefaultLanguage, SkosVocab.OpOrigin,
    };

    private static readonly HashSet<string> ConceptPredicates = new()
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

        var iri = data.Iri is { Length: > 0 } explicitIri
            ? explicitIri
            : $"{ks.VocabularyGraph}#concept-{Guid.NewGuid().ToString("N")[..16]}";

        var withScheme = data with { SchemeIri = schemeIri };
        var cleaned = ValidateConcept(ks, withScheme, excludeIri: null);
        if (GetConcept(ks, iri) is not null)
            throw new SkosValidationException("A concept with this IRI already exists");

        var statements = ConceptTriples(iri, cleaned, createdAt: NowIso(), graph: ks.VocabularyGraph);
        _store.AddStatements(ks, ks.VocabularyGraph, statements);
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
        var graph = ks.VocabularyGraph;
        RemovePredicates(ks, iri, ConceptPredicates);
        // Also drop any inbound `skos:related -> iri` triples.
        var inbound = _store.Match(ks, predicateIri: SkosVocab.Related,
            objectIri: iri, graphIri: graph);
        if (inbound.Count > 0) _store.RemoveStatements(ks, graph, inbound);
        var statements = ConceptTriples(iri, cleaned,
            createdAt: existing.CreatedAt.Length > 0 ? existing.CreatedAt : null, graph: graph);
        _store.AddStatements(ks, graph, statements);
        return iri;
    }

    /// <summary>Delete the concept + every triple that mentions its IRI.</summary>
    public int DeleteConcept(KsContext ks, string iri)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var graph = ks.VocabularyGraph;
        var inbound = _store.Match(ks, predicateIri: SkosVocab.Related,
            objectIri: iri, graphIri: graph);
        if (inbound.Count > 0) _store.RemoveStatements(ks, graph, inbound);
        return RemoveEntity(ks, graph, iri);
    }

    private void RemovePredicates(KsContext ks, string subject, HashSet<string> predicates)
    {
        var graph = ks.VocabularyGraph;
        foreach (var pred in predicates)
        {
            var existing = _store.Match(ks, subjectIri: subject, predicateIri: pred,
                graphIri: graph);
            if (existing.Count > 0) _store.RemoveStatements(ks, graph, existing);
        }
    }

    private int RemoveEntity(KsContext ks, string graph, string iri)
    {
        var outgoing = _store.Match(ks, subjectIri: iri, graphIri: graph);
        if (outgoing.Count > 0)
        {
            _store.RemoveStatements(ks, graph, outgoing);
        }
        return outgoing.Count;
    }

    private interface IVocabularyGraphStore
    {
        List<RdfStatement> Match(KsContext ks, string? subjectIri = null, string? predicateIri = null,
            string? objectIri = null, string? graphIri = null);
        void AddStatements(KsContext ks, string graphIri, IEnumerable<RdfStatement> statements);
        void RemoveStatements(KsContext ks, string graphIri, IEnumerable<RdfStatement> statements);
    }

    private sealed class PostgresVocabularyGraphStore : IVocabularyGraphStore
    {
        private readonly IRdfStatementRepository _statements;

        public PostgresVocabularyGraphStore(IRdfStatementRepository statements) => _statements = statements;

        public List<RdfStatement> Match(KsContext ks, string? subjectIri = null,
            string? predicateIri = null, string? objectIri = null, string? graphIri = null)
        {
            var selectedGraph = graphIri ?? ks.VocabularyGraph;
            return _statements.ListAsync(ks.KnowledgeSystemId, "Vocabulary").GetAwaiter().GetResult()
                .Where(s => s.GraphIri == selectedGraph)
                .Where(s => subjectIri is null || s.Subject is RdfIri iri && iri.Value == subjectIri)
                .Where(s => predicateIri is null || s.PredicateIri == predicateIri)
                .Where(s => objectIri is null || s.Object is RdfIri iri && iri.Value == objectIri)
                .ToList();
        }

        public void AddStatements(KsContext ks, string graphIri, IEnumerable<RdfStatement> statements)
        {
            var existing = Match(ks, graphIri: graphIri);
            var merged = existing.Concat(statements).Distinct().ToList();
            _statements.ReplaceLayerAsync(ks.KnowledgeSystemId, "Vocabulary", merged)
                .GetAwaiter().GetResult();
        }

        public void RemoveStatements(KsContext ks, string graphIri, IEnumerable<RdfStatement> statements)
        {
            var remove = statements.ToHashSet();
            var remaining = Match(ks, graphIri: graphIri)
                .Where(s => !remove.Contains(s))
                .ToList();
            _statements.ReplaceLayerAsync(ks.KnowledgeSystemId, "Vocabulary", remaining)
                .GetAwaiter().GetResult();
        }
    }

    private static List<RdfStatement> ConceptTriples(string iri, SkosConceptData data, string? createdAt, string graph)
    {
        var node = new RdfIri(iri);
        var now = NowIso();
        var statements = new List<RdfStatement>
        {
            new(node, Vocabulary.RdfType, new RdfIri(SkosVocab.Concept), graph),
            new(node, SkosVocab.InScheme, new RdfIri(data.SchemeIri), graph),
            new(node, SkosVocab.OpStatus, new RdfLiteral(data.Status), graph),
            new(node, SkosVocab.OpOrigin, new RdfLiteral(data.Origin), graph),
            new(node, SkosVocab.DcModified, new RdfLiteral(now), graph),
        };
        if (createdAt is { Length: > 0 })
            statements.Add(new RdfStatement(node, SkosVocab.DcCreated, new RdfLiteral(createdAt), graph));
        foreach (var l in new[] { data.PrefLabel })
        {
            statements.Add(new RdfStatement(node, SkosVocab.PrefLabel, MakeLiteral(l, data.Language), graph));
        }
        foreach (var l in data.EffectiveAltLabels)
        {
            statements.Add(new RdfStatement(node, SkosVocab.AltLabel, MakeLiteral(l.Value, l.Language), graph));
        }
        foreach (var l in data.EffectiveHiddenLabels)
        {
            statements.Add(new RdfStatement(node, SkosVocab.HiddenLabel, MakeLiteral(l.Value, l.Language), graph));
        }
        if (data.Description.Length > 0)
        {
            statements.Add(new RdfStatement(node, SkosVocab.Definition, MakeLiteral(data.Description, data.Language), graph));
        }
        if (data.Notation.Length > 0)
        {
            statements.Add(new RdfStatement(node, SkosVocab.Notation, new RdfLiteral(data.Notation), graph));
        }
        foreach (var parent in data.EffectiveBroader)
        {
            statements.Add(new RdfStatement(node, SkosVocab.Broader, new RdfIri(parent), graph));
        }
        foreach (var related in data.EffectiveRelated)
        {
            statements.Add(new RdfStatement(node, SkosVocab.Related, new RdfIri(related), graph));
            statements.Add(new RdfStatement(new RdfIri(related), SkosVocab.Related, node, graph));
        }
        if (!string.IsNullOrEmpty(data.MappedEntityIri))
        {
            statements.Add(new RdfStatement(node, SkosVocab.OpMapsTo, new RdfIri(data.MappedEntityIri), graph));
        }
        return statements;
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