using ISEStudio.Ontology;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Extraction;

/// <summary>
/// Counters produced by one <see cref="TerminologyService"/> sync pass. The
/// shape mirrors the Python backend's
/// <c>terminology_sync.sync_from_ontology</c> summary so the same downstream
/// review tooling can read either implementation.
/// </summary>
/// <param name="TermsAdded">New SKOS concepts created in the vocabulary graph.</param>
/// <param name="TermsMapped">
/// Class labels that already had a SKOS concept mapped to them and were
/// reported as such (not re-added).
/// </param>
/// <param name="ProposalsQueued">Concept suggestions queued for manual review.</param>
/// <param name="Error">Set when the sync encountered an error; never propagated.</param>
/// <param name="SchemeIri">
/// IRI of the <c>skos:ConceptScheme</c> the deterministic sync anchored
/// its new concepts to, when one was resolved. Mirrors the Python
/// backend's <c>sync_result["scheme_iri"]</c>. <c>null</c> when the TBox
/// had no entities or the sync short-circuited. The extraction-job
/// orchestrator reads this to feed
/// <see cref="TerminologyAgent.SuggestAsync"/>.
/// </param>
/// <param name="Properties">
/// Property entities (object + data) the sync observed in the TBox. Mirrors
/// Python's <c>entities = classes + object_properties + data_properties</c>
/// aggregate. Surfaced in the audit row so reviewers can spot TBoxes that
/// have properties without classes (or vice versa) without re-querying.
/// </param>
/// <param name="AliasesAdded">
/// Concepts that gained an additional <c>skos:altLabel</c> triple during this
/// pass because the entity's normalised label was not yet attached. Mirrors
/// Python's <c>result["aliases_added"]</c>.
/// </param>
/// <param name="BroaderAdded">
/// <c>skos:broader</c> triple additions seeded from <c>rdfs:subClassOf</c>
/// relations among mapped classes. Mirrors Python's
/// <c>result["broader_added"]</c>; only relations whose endpoints share a
/// scheme are counted.
/// </param>
/// <param name="StaleMappingsRemoved">
/// Concepts whose <c>op:mapsTo</c> was cleared — they had previously pointed
/// at an ontology / ABox IRI that no longer exists. Mirrors Python's
/// <c>result["stale_mappings_removed"]</c>; the concept row is preserved so
/// a human can remap or deprecate it.
/// </param>
/// <param name="MappingConflicts">
/// Entities whose label collided with a concept already mapped to a
/// different ontology IRI. The sync skipped them (no write) so the existing
/// mapping wins; the reviewer can resolve manually. Mirrors Python's
/// <c>result["mapping_conflicts"]</c>.
/// </param>
public sealed record TerminologyResult(
    int TermsAdded,
    int TermsMapped,
    int ProposalsQueued,
    string? Error,
    string? SchemeIri = null,
    int Properties = 0,
    int AliasesAdded = 0,
    int BroaderAdded = 0,
    int StaleMappingsRemoved = 0,
    int MappingConflicts = 0)
{
    /// <summary>All-zero summary used when sync is skipped.</summary>
    public static TerminologyResult Zero { get; } = new(0, 0, 0, null, null, 0, 0, 0, 0, 0);
}

/// <summary>
/// Deterministic SKOS terminology sync. Mirrors
/// <c>backend/app/ontology/terminology_sync.py::sync_from_ontology</c> at the
/// level the orchestrator's <c>terms_added</c> / <c>terms_mapped</c> /
/// <c>terminology_proposals</c> / <c>terminology_error</c> columns require:
/// for every class label in the TBox, ensure a SKOS concept exists in the
/// vocabulary graph (creating one when missing) and count whether it was a
/// fresh addition or already mapped.
///
/// <para>The sync never throws — a vocabulary graph error becomes
/// <see cref="TerminologyResult.Error"/> so the extraction run still
/// completes. That mirrors the Python worker where the terminology stage is
/// advisory rather than required.</para>
/// </summary>
/// <remarks>
/// <para>The deterministic sync itself never produces proposals — LLM
/// suggestions are queued by
/// <see cref="ExtractionOrchestrator.RunTerminologyAsync"/> via
/// <see cref="TerminologyAgent.SuggestAsync"/>, which reads the
/// <see cref="SchemeIri"/> this pass returns and folds the resulting count
/// into <see cref="ProposalsQueued"/> before the job row is written.</para>
/// </remarks>
public sealed class TerminologyService : ITerminologySync
{
    private readonly StoreWrapper? _store;
    private readonly TimeProvider _clock;

    // The store is optional so the contract-test factory (which registers
    // a null StoreWrapper when no RocksDB root is provisioned) can still
    // resolve this service. When the store is null, sync returns the
    // zero summary and the vocabulary layer sees an empty graph; the
    // public contract shape is preserved so the HTTP envelope still
    // parses cleanly.
    public TerminologyService(StoreWrapper? store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Run one sync pass against the TBox graph and vocabulary graph.</summary>
    public TerminologyResult SyncAsync(KsContext ks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ks);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return SyncCore(ks, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TerminologyResult(0, 0, 0, ex.Message, null);
        }
    }

    private TerminologyResult SyncCore(KsContext ks, CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            // No graph store wired (contract-test path) — vocabulary
            // layer has nothing to scan or write, so report the
            // deterministic zero summary.
            return TerminologyResult.Zero;
        }
        var view = SchemaBuilder.BuildView(ks.TBoxGraph, _store);

        // Python parity: `entities = classes + object_properties + data_properties`.
        // The property count surfaces separately in the audit row so reviewers
        // can spot TBoxes that have properties without classes (or vice versa)
        // without re-querying the schema builder.
        var ontologyIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in view.Classes) ontologyIris.Add(c.Iri);
        foreach (var p in view.ObjectProperties) ontologyIris.Add(p.Iri);
        foreach (var p in view.DataProperties) ontologyIris.Add(p.Iri);
        var propertyCount = view.ObjectProperties.Count + view.DataProperties.Count;

        if (ontologyIris.Count == 0) return TerminologyResult.Zero;

        // Ensure a default ConceptScheme exists so the vocabulary view has a
        // scheme to anchor the concepts this pass creates. Mirrors the Python
        // backend's ensure_scheme(): reuse the fixed "#scheme-extracted" IRI
        // (or the single / extraction / most-mapped existing scheme), and
        // create it when the vocabulary graph has none yet. Without this a
        // freshly-extracted knowledge system reports scheme_count=0 with a
        // fully-populated concept list, leaving the "New term" button
        // permanently disabled (empty selectedSchemeIri).
        var schemeIri = EnsureScheme(ks, view);
        if (schemeIri is null)
        {
            return new TerminologyResult(0, 0, 0, null, null,
                Properties: propertyCount, AliasesAdded: 0, BroaderAdded: 0,
                StaleMappingsRemoved: 0, MappingConflicts: 0);
        }

        var skos = new SkosManager(_store);

        // ---- Pass 1: stale mappings ----
        // Mirrors Python `terminology_sync.sync_from_ontology` lines 122-130:
        // any concept whose `mappedEntityIri` no longer exists in either the
        // ontology or the ABox gets its mapping cleared (but the concept row
        // itself is preserved so a human can remap or deprecate it).
        // `valid_mapping_iris = ontology_iris | abox_iris` — the ABox half
        // reads the subject set of the `…/abox` named graph (every instance
        // IRI), mirroring `store.read_triples(abox_iri)`.
        var aboxIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in _store.Match(graph: new OntoNamedNode(ks.ABoxGraph)))
        {
            if (q.Subject is OntoNamedNode n) aboxIris.Add(n.Value);
        }
        var validMappingIris = new HashSet<string>(ontologyIris, StringComparer.Ordinal);
        validMappingIris.UnionWith(aboxIris);

        var staleMappingsRemoved = 0;
        var preView = skos.BuildView(ks);
        foreach (var concept in preView.Concepts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(concept.MappedEntityIri)) continue;
            if (validMappingIris.Contains(concept.MappedEntityIri!)) continue;
            // The mapping target no longer exists in the ontology or ABox;
            // preserve the concept but drop the op:mapsTo triple so the
            // reviewer can decide (remap or deprecate). Python's
            // update_concept rewrites the whole concept payload; the
            // minimal RemoveQuads here produces the same final graph state
            // without the round-trip.
            var stale = _store.Match(
                subjectIri: concept.Iri,
                predicateIri: SkosVocab.OpMapsTo.Value,
                graphIri: ks.VocabularyGraph);
            if (stale.Count > 0) _store.RemoveQuads(new OntoNamedNode(ks.VocabularyGraph), stale);
            staleMappingsRemoved++;
        }

        // ---- Pass 2: entity sync ----
        // Python decision tree per entity (mirrors `terminology_sync`):
        //   1. `concept_by_mapping[iri]` exists → entity already has a
        //      mapped concept; nothing to create, the alias pass below
        //      attaches the entity label if it's missing.
        //   2. the entity's label is owned by a mapped concept pointing at a
        //      different IRI → `mapping_conflicts += 1; continue`.
        //   3. the entity's label exists as a pref-label on an unmapped
        //      concept → map that concept onto the entity (`terms_mapped`).
        //   4. otherwise create a fresh mapped concept (`terms_added` and
        //      `terms_mapped`).
        //
        // `conceptByMapping` mirrors Python's `concept_by_mapping` dict;
        // `mappedIndex` mirrors the mapped subset of `label_owner`
        // (via MappedAliases). Both are refreshed after each create so a
        // re-encountered entity in the same pass sees the new state.
        var conceptByMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var mappedIndex = new Dictionary<string, string>(skos.MappedAliases(ks), StringComparer.Ordinal);
        foreach (var c in preView.Concepts)
        {
            if (!string.IsNullOrEmpty(c.MappedEntityIri))
                conceptByMapping[c.MappedEntityIri!] = c.Iri;
        }

        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var now = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var added = 0;
        var mapped = 0;
        var mappingConflicts = 0;

        // Iterate classes first (the order the previous version of this
        // method used), then properties — Python aggregates them in the same
        // order via `dict(entity, entity_kind="class"|"object_property"|...)`.
        foreach (var entity in EnumerateEntities(view))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (iri, label) = entity;
            var normalized = Vocabulary.NormLabel(label);
            if (normalized.Length == 0) continue;

            // Branch 1: entity already has a mapped concept.
            if (conceptByMapping.ContainsKey(iri)) continue;

            // Branch 2: label owned by a mapped concept at a different IRI.
            if (mappedIndex.TryGetValue(normalized, out _))
            {
                mappingConflicts++;
                continue;
            }

            // Branch 3: label exists as an unmapped pref-label → adopt it.
            var existingConcept = FindConceptIriByPrefLabel(ks, label);
            if (existingConcept is not null)
            {
                _store.AddQuads(graph, new[]
                {
                    new Oxigraph.Quad(
                        new OntoNamedNode(existingConcept),
                        SkosVocab.OpMapsTo,
                        new OntoNamedNode(iri),
                        graph),
                });
                mapped++;
                mappedIndex[normalized] = iri;
                conceptByMapping[iri] = existingConcept;
                continue;
            }

            // Branch 4: fresh mapped concept. The label language mirrors
            // Python's `_language(label)` CJK heuristic ("zh-CN" for CJK
            // labels, "en" otherwise) so Chinese TBoxes mint Chinese
            // pref labels.
            var concept = new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(iri)}");
            _store.AddQuads(graph, new[]
            {
                new Oxigraph.Quad(concept, Vocabulary.RdfType, SkosVocab.Concept, graph),
                new Oxigraph.Quad(concept, SkosVocab.InScheme, new OntoNamedNode(schemeIri), graph),
                new Oxigraph.Quad(concept, SkosVocab.PrefLabel, new OntoLiteral(label, ContainsCjk(label) ? "zh-CN" : "en"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpStatus, new OntoLiteral("active"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpMapsTo, new OntoNamedNode(iri), graph),
                new Oxigraph.Quad(concept, SkosVocab.DcCreated, new OntoLiteral(now), graph),
            });
            // Python parity: a fresh concept is both `terms_added` and
            // `terms_mapped` (its mapping exists by construction).
            added++;
            mapped++;
            mappedIndex[normalized] = iri;
            conceptByMapping[iri] = concept.Value;
        }

        // ---- Pass 3: alias additions ----
        // For every mapped concept, ensure its entity's normalised label is
        // attached as at least one of `pref_labels` / `alt_labels` /
        // `hidden_labels`. Mirrors Python's
        // `result["aliases_added"] += 1` increment after the
        // `existing_keys / label_owner` dedup loop.
        //
        // Python parity notes:
        // - `label_owner` contains only labels of concepts in the resolved
        //   scheme, so an alias that another concept in the SAME scheme
        //   already owns is skipped (`key not in label_owner`).
        // - Python rewrites the whole concept via update_concept; we add the
        //   single `skos:altLabel` triple directly. Final graph state is
        //   identical and the minimal write avoids SkosManager's
        //   single-prefLabel round-trip (which would drop extra-language
        //   pref labels on concepts this sync did not create).
        var aliasesAdded = 0;
        var postView = skos.BuildView(ks);
        var labelOwners = new HashSet<(string Norm, string Lang)>(NormLangOrdinalComparer.Instance);
        foreach (var c in postView.Concepts)
        {
            if (c.SchemeIri != schemeIri) continue;
            foreach (var l in c.PrefLabels.Concat(c.AltLabels).Concat(c.HiddenLabels))
                labelOwners.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
        }
        var entityIndex = BuildEntityIndex(view);
        foreach (var concept in postView.Concepts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(concept.MappedEntityIri)) continue;
            if (!entityIndex.TryGetValue(concept.MappedEntityIri!, out var entity)) continue;
            var (label, lang) = entity;
            var key = (Vocabulary.NormLabel(label), lang.ToLowerInvariant());
            var existing = new HashSet<(string Norm, string Lang)>(NormLangOrdinalComparer.Instance);
            foreach (var l in concept.PrefLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            foreach (var l in concept.AltLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            foreach (var l in concept.HiddenLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            if (existing.Contains(key)) continue;
            if (labelOwners.Contains(key)) continue;
            _store.AddQuads(new OntoNamedNode(ks.VocabularyGraph), new[]
            {
                new Oxigraph.Quad(
                    new OntoNamedNode(concept.Iri),
                    SkosVocab.AltLabel,
                    new OntoLiteral(label, lang),
                    new OntoNamedNode(ks.VocabularyGraph)),
            });
            aliasesAdded++;
            // Python refreshes `label_owner[key] = concept` after each
            // alias write so a second concept mapped to the same entity
            // does not attach the same label again.
            labelOwners.Add(key);
        }

        // ---- Pass 4: broader additions ----
        // For every class with a superclass relation, add the corresponding
        // mapped parent concept's IRI to its `skos:broader` set (mirrors
        // Python `result["broader_added"] += len(additions)`).
        // Relations spanning different schemes, self-loops, and already-
        // present entries are skipped. Python funnels the whole batch
        // through update_concept (cycle check); we add each triple directly
        // because the same-scheme + non-self filters above already exclude
        // every relation the SKOS validator would reject except a cycle,
        // which the schema builder's subclass view does not produce.
        var broaderAdded = 0;
        var finalView = skos.BuildView(ks);
        foreach (var cls in view.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cls.Superclasses.Count == 0) continue;
            var concept = finalView.Concepts.FirstOrDefault(c => c.MappedEntityIri == cls.Iri);
            if (concept is null) continue;
            var additions = new List<string>();
            foreach (var parentIri in cls.Superclasses)
            {
                var parentConcept = finalView.Concepts.FirstOrDefault(c => c.MappedEntityIri == parentIri);
                if (parentConcept is null) continue;
                if (parentConcept.SchemeIri != concept.SchemeIri) continue;
                if (parentConcept.Iri == concept.Iri) continue;
                if (concept.Broader.Contains(parentConcept.Iri, StringComparer.Ordinal)) continue;
                additions.Add(parentConcept.Iri);
            }
            if (additions.Count == 0) continue;
            var broaderQuads = new List<Oxigraph.Quad>(additions.Count);
            foreach (var parent in additions)
            {
                broaderQuads.Add(new Oxigraph.Quad(
                    new OntoNamedNode(concept.Iri),
                    SkosVocab.Broader,
                    new OntoNamedNode(parent),
                    new OntoNamedNode(ks.VocabularyGraph)));
            }
            _store.AddQuads(new OntoNamedNode(ks.VocabularyGraph), broaderQuads);
            broaderAdded += additions.Count;
        }

        return new TerminologyResult(
            TermsAdded: added,
            TermsMapped: mapped,
            ProposalsQueued: 0,
            Error: null,
            SchemeIri: schemeIri,
            Properties: propertyCount,
            AliasesAdded: aliasesAdded,
            BroaderAdded: broaderAdded,
            StaleMappingsRemoved: staleMappingsRemoved,
            MappingConflicts: mappingConflicts);
    }

    /// <summary>
    /// Enumerate the ontology's classes + properties as a uniform sequence of
    /// (Iri, Label) tuples so the entity loop can iterate without dispatching
    /// on the concrete view type. Python parity: the source aggregate is
    /// <c>classes + object_properties + data_properties</c> in that order.
    /// </summary>
    private static IEnumerable<(string Iri, string Label)> EnumerateEntities(OntologyView view)
    {
        foreach (var c in view.Classes)
            yield return (c.Iri, c.Label);
        foreach (var p in view.ObjectProperties)
            yield return (p.Iri, p.Label);
        foreach (var p in view.DataProperties)
            yield return (p.Iri, p.Label);
    }

    /// <summary>
    /// Reverse-lookup from IRI → (Label, Language) for every ontology entity.
    /// Used by the alias-addition pass to find each mapped concept's source
    /// entity's label so it can re-attach it as an <c>skos:altLabel</c>.
    /// Language mirrors Python's <c>_language(label)</c> CJK heuristic.
    /// </summary>
    private static Dictionary<string, (string Label, string Language)> BuildEntityIndex(OntologyView view)
    {
        var index = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var c in view.Classes)
            index[c.Iri] = (c.Label, ContainsCjk(c.Label) ? "zh-CN" : "en");
        foreach (var p in view.ObjectProperties)
            index[p.Iri] = (p.Label, ContainsCjk(p.Label) ? "zh-CN" : "en");
        foreach (var p in view.DataProperties)
            index[p.Iri] = (p.Label, ContainsCjk(p.Label) ? "zh-CN" : "en");
        return index;
    }

    /// <summary>
    /// Idempotently resolve (or create) the ConceptScheme the deterministic
    /// sync anchors its fresh concepts to. Mirrors Python
    /// <c>terminology_sync.ensure_scheme</c>:
    /// <list type="number">
    /// <item>the fixed <c>#scheme-extracted</c> IRI if it already exists;</item>
    /// <item>the sole scheme when exactly one exists;</item>
    /// <item>the largest <c>origin=extraction</c> scheme;</item>
    /// <item>the scheme with the most mapped concepts (ties by concept count);</item>
    /// <item>otherwise create a fresh default scheme titled from the KS name.</item>
    /// </list>
    /// Returns <c>null</c> when the TBox has no entities (nothing to anchor).
    /// </summary>
    private string? EnsureScheme(KsContext ks, OntologyView ontology)
    {
        if (ontology.Classes.Count + ontology.ObjectProperties.Count + ontology.DataProperties.Count == 0)
            return null;

        var view = new SkosManager(_store).BuildView(ks);
        var fixedIri = $"{ks.VocabularyGraph}#scheme-extracted";
        var fixedScheme = view.Schemes.FirstOrDefault(s => s.Iri == fixedIri);
        if (fixedScheme is not null) return fixedScheme.Iri;
        if (view.Schemes.Count == 1) return view.Schemes[0].Iri;

        var generated = view.Schemes.Where(s => s.Origin == "extraction").ToList();
        if (generated.Count > 0)
            return generated.OrderByDescending(s => s.ConceptCount).First().Iri;

        if (view.Schemes.Count > 0)
        {
            var mappedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in view.Concepts)
            {
                if (!string.IsNullOrEmpty(c.MappedEntityIri))
                    mappedCounts[c.SchemeIri] = mappedCounts.GetValueOrDefault(c.SchemeIri, 0) + 1;
            }
            return view.Schemes
                .OrderByDescending(s => mappedCounts.GetValueOrDefault(s.Iri, 0))
                .ThenByDescending(s => s.ConceptCount)
                .First().Iri;
        }

        // No schemes yet — create the default extracted scheme so the
        // vocabulary surface reports scheme_count >= 1.
        var (title, description, language) = SchemeTitle(ks.Name);
        return new SkosManager(_store).CreateScheme(ks, new SkosSchemeData(
            Iri: fixedIri,
            Title: title,
            DefaultLanguage: language,
            Description: description,
            Origin: "extraction"));
    }

    /// <summary>
    /// Derive the default scheme's title / description / language from the KS
    /// name, matching Python <c>terminology_sync._scheme_title</c> (Chinese
    /// names get a Chinese title, everything else an English one).
    /// </summary>
    private static (string Title, string Description, string Language) SchemeTitle(string ksName)
    {
        var language = ContainsCjk(ksName) ? "zh-CN" : "en";
        return language == "zh-CN"
            ? ($"{ksName}术语表", "随本体抽取自动形成，并由人工持续治理的受控词表。", language)
            : ($"{ksName} terminology",
                "Controlled terminology formed automatically during ontology extraction and governed by humans.",
                language);
    }

    /// <summary>True when <paramref name="text"/> contains a CJK Unified Ideograph.</summary>
    private static bool ContainsCjk(string? text)
    {
        foreach (var c in text ?? "")
        {
            if (c >= '㐀' && c <= '鿿') return true;
        }
        return false;
    }

    /// <summary>
    /// Find a concept IRI in the vocabulary graph whose <c>skos:prefLabel</c>
    /// normalises to <paramref name="label"/>. Returns <c>null</c> when no
    /// concept advertises the label. Mirrors Python's
    /// <c>label_owner.get(key)</c> lookup for the unmapped-exact branch
    /// (branch 3 of the entity loop).
    /// </summary>
    private string? FindConceptIriByPrefLabel(KsContext ks, string label)
    {
        if (_store is null) return null;
        var existing = _store.Match(
            predicateIri: SkosVocab.PrefLabel.Value,
            graphIri: ks.VocabularyGraph);
        var normalized = Vocabulary.NormLabel(label);
        foreach (var quad in existing)
        {
            if (quad.Object is OntoLiteral l &&
                string.Equals(Vocabulary.NormLabel(l.Value), normalized, StringComparison.Ordinal))
            {
                return quad.Subject is OntoNamedNode n ? n.Value : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Extract the local name from an IRI: everything after the last <c>#</c>
    /// (or last <c>/</c> when the IRI uses slash-style local names). This is
    /// used to mint per-class concept IRIs inside the vocabulary graph;
    /// pulling everything after the last slash would re-introduce the
    /// hash-encoded local name (e.g. <c>onto#Animal</c>), which is invalid
    /// as a fragment name.
    /// </summary>
    private static string LocalName(string iri)
    {
        var hash = iri.LastIndexOf('#');
        var slash = iri.LastIndexOf('/');
        var cut = hash > slash ? hash : slash;
        return cut >= 0 ? iri[(cut + 1)..] : iri;
    }

    /// <summary>
    /// Ordinal-comparer for the (normalised label, language) tuples the
    /// alias-dedup pass uses. <see cref="StringComparer.Ordinal"/> does
    /// not implement <see cref="IEqualityComparer{T}"/> for value tuples
    /// directly, so we wrap it.
    /// </summary>
    private sealed class NormLangOrdinalComparer : IEqualityComparer<(string Norm, string Lang)>
    {
        public static readonly NormLangOrdinalComparer Instance = new();
        public bool Equals((string Norm, string Lang) x, (string Norm, string Lang) y) =>
            string.Equals(x.Norm, y.Norm, StringComparison.Ordinal)
            && string.Equals(x.Lang, y.Lang, StringComparison.Ordinal);
        public int GetHashCode((string Norm, string Lang) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Norm ?? string.Empty),
                StringComparer.Ordinal.GetHashCode(obj.Lang ?? string.Empty));
    }
}