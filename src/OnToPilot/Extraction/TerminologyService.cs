using OnToPilot.Ontology;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Extraction;

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
public sealed record TerminologyResult(
    int TermsAdded,
    int TermsMapped,
    int ProposalsQueued,
    string? Error,
    string? SchemeIri = null)
{
    /// <summary>All-zero summary used when sync is skipped.</summary>
    public static TerminologyResult Zero { get; } = new(0, 0, 0, null, null);
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
        var classes = view.Classes;
        if (classes.Count == 0) return TerminologyResult.Zero;

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
            return new TerminologyResult(0, 0, 0, null, null);
        }

        // Index existing mapped concepts by their pref-label so a re-run
        // counts as mapped rather than re-added.
        var mappedIndex = new SkosManager(_store).MappedAliases(ks);

        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var now = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var added = 0;
        var mapped = 0;

        foreach (var cls in classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Vocabulary.NormLabel(cls.Label);
            if (normalized.Length == 0) continue;

            // A mapped alias means a SKOS concept already points at this
            // class IRI through its `mappedEntityIri`. No write required.
            if (mappedIndex.ContainsKey(normalized))
            {
                mapped++;
                continue;
            }

            // Otherwise check whether a pref-label without a mapping exists;
            // if so, leave it alone (we don't own the prior data) and only
            // mint fresh concepts when no concept advertises the label.
            if (PrefLabelExists(ks, cls.Label)) continue;

            var concept = new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}");
            _store.AddQuads(graph, new[]
            {
                new Oxigraph.Quad(concept, Vocabulary.RdfType, SkosVocab.Concept, graph),
                new Oxigraph.Quad(concept, SkosVocab.InScheme, new OntoNamedNode(schemeIri), graph),
                new Oxigraph.Quad(concept, SkosVocab.PrefLabel, new OntoLiteral(cls.Label, "en"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpStatus, new OntoLiteral("active"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpMapsTo, new OntoNamedNode(cls.Iri), graph),
                new Oxigraph.Quad(concept, SkosVocab.DcCreated, new OntoLiteral(now), graph),
            });
            added++;
        }

        return new TerminologyResult(added, mapped, ProposalsQueued: 0, Error: null, SchemeIri: schemeIri);
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

    private bool PrefLabelExists(KsContext ks, string label)
    {
        if (_store is null) return false;
        var existing = _store.Match(
            predicateIri: SkosVocab.PrefLabel.Value,
            graphIri: ks.VocabularyGraph);
        var normalized = Vocabulary.NormLabel(label);
        foreach (var quad in existing)
        {
            if (quad.Object is OntoLiteral l &&
                string.Equals(Vocabulary.NormLabel(l.Value), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
}