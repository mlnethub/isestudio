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
public sealed record TerminologyResult(
    int TermsAdded,
    int TermsMapped,
    int ProposalsQueued,
    string? Error)
{
    /// <summary>All-zero summary used when sync is skipped.</summary>
    public static TerminologyResult Zero { get; } = new(0, 0, 0, null);
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
/// <para>Unlike the Python backend, the .NET port does not yet run an LLM
/// proposal stage. <see cref="ProposalsQueued"/> is therefore always zero —
/// the column stays in the contract so later tasks can drop in an LLM
/// stage without a schema change.</para>
/// </remarks>
public sealed class TerminologyService : ITerminologySync
{
    private readonly StoreWrapper _store;
    private readonly TimeProvider _clock;

    public TerminologyService(StoreWrapper store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
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
            return new TerminologyResult(0, 0, 0, ex.Message);
        }
    }

    private TerminologyResult SyncCore(KsContext ks, CancellationToken cancellationToken)
    {
        var view = SchemaBuilder.BuildView(ks.TBoxGraph, _store);
        var classes = view.Classes;
        if (classes.Count == 0) return TerminologyResult.Zero;

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

            _store.AddQuads(graph, new[]
            {
                new Oxigraph.Quad(
                    new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}"),
                    Vocabulary.RdfType,
                    SkosVocab.Concept,
                    graph),
                new Oxigraph.Quad(
                    new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}"),
                    SkosVocab.PrefLabel,
                    new OntoLiteral(cls.Label, "en"),
                    graph),
                new Oxigraph.Quad(
                    new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}"),
                    SkosVocab.OpStatus,
                    new OntoLiteral("active"),
                    graph),
                new Oxigraph.Quad(
                    new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}"),
                    SkosVocab.OpMapsTo,
                    new OntoNamedNode(cls.Iri),
                    graph),
                new Oxigraph.Quad(
                    new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(cls.Iri)}"),
                    SkosVocab.DcCreated,
                    new OntoLiteral(now),
                    graph),
            });
            added++;
        }

        return new TerminologyResult(added, mapped, ProposalsQueued: 0, Error: null);
    }

    private bool PrefLabelExists(KsContext ks, string label)
    {
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