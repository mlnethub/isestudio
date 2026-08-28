using ISEStudio.Application.Foundation;
using ISEStudio.Application.Vocabulary;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application service for the twenty-eight vocabulary dispatcher arms.
/// Sixteen internal <c>vocabulary.*</c> ops + four <c>external.vocabulary.*</c>
/// + four <c>published.vocabulary.*</c> + four
/// <c>published.release.vocabulary.*</c>. The four cross-surface arms share
/// the same Vocabulary read services (the Reader gate enforces access);
/// published.release pins to a specific release in a future slice.
///
/// <para>Each method unpacks one <see cref="InternalRequest"/> (path /
/// query / body / actor), delegates to the underlying domain service, and
/// returns the strongly-typed DTO the dispatcher serialises &mdash; or
/// <c>null</c> when the operation has no body / no resource id.</para>
///
/// <para><b>Special return shapes.</b></para>
/// <list type="bullet">
/// <item><c>VocabularyListSchemesAsync</c> projects
/// <see cref="SkosView"/> down to <c>{items, total, stats}</c> &mdash;
/// <c>SkosView</c> already has schemes + stats; the per list response
/// embeds the stats envelope so callers don't have to make a second
/// roundtrip.</item>
/// <item><c>VocabularyListConceptsAsync</c> / <c>ResolveTermAsync</c> /
/// <c>SuggestTermsAsync</c> project <c>(items, total)</c> tuples /
/// <c>IReadOnlyList</c>s to <c>{items, total}</c> envelopes matching the
/// Python list shape.</item>
/// <item><c>VocabularyDeleteSchemeAsync</c> /
/// <c>VocabularyDeleteConceptAsync</c> project
/// <c>(deleted, removed_triples)</c> tuples to the
/// <c>{deleted, removed_triples}</c> envelope.</item>
/// <item><c>VocabularyExportAsync</c> / the four cross-surface export
/// helpers return a UTF-8 <c>string</c> (the dispatcher layers the
/// <c>string.Empty</c> fallback when the service is missing).</item>
/// <item><c>VocabularyAcceptProposalAsync</c> wraps the
/// <c>(proposal, concept)</c> tuple in a typed envelope so the
/// dispatcher can serialise both halves without reflection.</item>
/// <item><c>VocabularySyncAsync</c> returns the inner
/// <see cref="ISEStudio.Extraction.TerminologyResult"/> from
/// <c>VocabularyService.SyncAsync</c>; the dispatcher falls back to the
/// <c>EmptySyncResponse()</c> shape when the service is missing.</item>
/// </list>
/// </summary>
public interface IVocabularyApplicationService
{
    // ----------------------------------------------------------------------
    // Internal vocabulary — 16 op
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>vocabulary.get</c> &mdash; the full SKOS view (schemes + concepts
    /// + stats) for one KS. Reads <c>request.KnowledgeSystemGuid</c>.
    /// Returns <c>null</c> when the KS is missing or the service isn't
    /// registered; dispatcher maps to <c>EmptyVocabularyResponse()</c>.
    /// </summary>
    Task<SkosView?> GetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.list_schemes</c> &mdash; the schemes list (with
    /// roll-up stats) for one KS. Reads <c>request.KnowledgeSystemGuid</c>.
    /// Returns <c>null</c> when the KS is missing or the service isn't
    /// registered; dispatcher maps to <c>EmptyVocabularySchemeList()</c>.
    /// </summary>
    Task<object?> ListSchemesAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.list_concepts</c> &mdash; paged concept list with
    /// optional <c>scheme_iri</c> / <c>q</c> / <c>status</c> /
    /// <c>mapping</c> / <c>origin</c> filters and <c>limit</c> /
    /// <c>offset</c> paging. Returns the <c>{items, total}</c> envelope
    /// (already projected from <see cref="SkosConceptPage"/>) or
    /// <c>null</c> when the KS is missing.
    /// </summary>
    Task<object?> ListConceptsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.resolve_term</c> &mdash; ranked concept matches for a
    /// free-text query. Reads <c>q</c> + <c>language</c> + <c>limit</c>
    /// from the query string. Returns <c>{items, total}</c> envelope or
    /// <c>null</c> when the KS is missing.
    /// </summary>
    Task<object?> ResolveTermAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.export</c> &mdash; vocabulary graph as RDF bytes.
    /// Reads <c>fmt</c> from the query string (default <c>n-quads</c>);
    /// the dispatcher hands the bytes back as a UTF-8 string for the
    /// default <c>application/o-xt-json</c> envelope. Returns
    /// <see cref="string.Empty"/> when the service is missing or returns
    /// null.
    /// </summary>
    Task<string?> ExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.create_scheme</c> &mdash; open a new SKOS
    /// ConceptScheme in the vocabulary graph. Body carries
    /// <see cref="SkosSchemeData"/>.
    /// </summary>
    Task<SkosSchemeView?> CreateSchemeAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.update_scheme</c> &mdash; replace the scheme-
    /// predicate set. The route id (when present) carries the scheme IRI;
    /// body carries the new <see cref="SkosSchemeData"/>.
    /// </summary>
    Task<SkosSchemeView?> UpdateSchemeAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.delete_scheme</c> &mdash; delete the scheme + every
    /// concept that referenced it. Route id carries the scheme IRI.
    /// Returns <c>{deleted, removed_triples}</c> envelope or <c>null</c>
    /// when the KS is missing.
    /// </summary>
    Task<object?> DeleteSchemeAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.create_concept</c> &mdash; open a new SKOS Concept
    /// under <c>scheme_iri</c> (read from the body's
    /// <see cref="SkosConceptData.SchemeIri"/>). Body carries the full
    /// <see cref="SkosConceptData"/>.
    /// </summary>
    Task<SkosConceptView?> CreateConceptAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.update_concept</c> &mdash; replace the concept-
    /// predicate set. The IRI is read from <c>data.Iri</c> first, then
    /// <c>request.ResourceId</c> as a fallback.
    /// </summary>
    Task<SkosConceptView?> UpdateConceptAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.delete_concept</c> &mdash; delete the concept + every
    /// triple that mentions its IRI. The IRI is read from
    /// <see cref="InternalRequestHelpers.ExtractBodyIri"/> first, then
    /// <c>request.ResourceId</c>. Returns <c>{deleted, removed_triples}</c>
    /// envelope or <c>null</c> when the KS is missing.
    /// </summary>
    Task<object?> DeleteConceptAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.sync</c> &mdash; run the deterministic SKOS
    /// terminology sync against the KS TBox + vocabulary graphs. Returns
    /// the <see cref="ISEStudio.Extraction.TerminologyResult"/> or
    /// <c>null</c> when the KS is missing.
    /// </summary>
    Task<object?> SyncAsync(InternalRequest request, CancellationToken cancellationToken);

    // ----------------------------------------------------------------------
    // Proposals + suggest — 4 op
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>vocabulary.list_proposals</c> &mdash; page through
    /// <c>TermProposalEntity</c> rows. Reads <c>status</c> / <c>q</c> /
    /// <c>limit</c> / <c>offset</c> from the query string. Returns the
    /// <c>{items, total}</c> envelope or <c>null</c> when the KS is missing.
    /// </summary>
    Task<object?> ListProposalsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.accept_proposal</c> &mdash; apply a pending proposal
    /// to the SKOS vocabulary graph. Body carries an optional
    /// <c>payload</c> override + optional <c>note</c>; route id carries
    /// the proposal id. Returns the
    /// <c>{proposal, concept}</c> envelope or <c>null</c> when the KS
    /// or proposal id is missing.
    /// </summary>
    Task<object?> AcceptProposalAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.reject_proposal</c> &mdash; mark a pending proposal
    /// <c>rejected</c> without writing to the SKOS graph. Body carries
    /// an optional <c>note</c>; route id carries the proposal id.
    /// Returns the rejected <see cref="ISEStudio.Infrastructure.Persistence.Entities.TermProposalEntity"/>
    /// or <c>null</c> when the KS or proposal id is missing.
    /// </summary>
    Task<object?> RejectProposalAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>vocabulary.suggest_terms</c> &mdash; run an LLM-driven
    /// controlled-terminology proposal pass. Body carries
    /// <c>scheme_iri</c> + optional <c>model</c> + <c>chunk_ids</c>.
    /// Returns the <c>{items, total}</c> envelope of newly inserted
    /// <c>TermProposalEntity</c> rows (typed as
    /// <see cref="object"/> since <c>TermProposalEntity</c> is a domain
    /// entity, not a wire DTO).
    /// </summary>
    Task<object?> SuggestTermsAsync(InternalRequest request, CancellationToken cancellationToken);

    // ----------------------------------------------------------------------
    // Cross-surface (publicId-keyed) — 4 op, shared by external /
    // published / published.release vocabulary routes
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>external.vocabulary.concepts</c> /
    /// <c>published.vocabulary.concepts</c> /
    /// <c>published.release.vocabulary.concepts</c> &mdash; paged concept
    /// list keyed by public id. Same filter set as the internal arm; reads
    /// <c>request.PublicId</c>. Returns the <c>{items, total}</c> envelope.
    /// </summary>
    Task<object?> ListConceptsPublishedAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>external.vocabulary.export</c> /
    /// <c>published.vocabulary.export</c> /
    /// <c>published.release.vocabulary.export</c> &mdash; vocabulary graph
    /// as RDF bytes, keyed by public id. Reads <c>fmt</c> from the query
    /// string (default <c>n-quads</c>). Returns the UTF-8 string body or
    /// <see cref="string.Empty"/>.
    /// </summary>
    Task<string?> ExportPublishedAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>external.vocabulary.resolve</c> /
    /// <c>published.vocabulary.resolve</c> /
    /// <c>published.release.vocabulary.resolve</c> &mdash; ranked concept
    /// matches for a free-text query, keyed by public id. Reads <c>q</c>
    /// + <c>language</c> + <c>limit</c> from the query string. Returns
    /// the <c>{items, total}</c> envelope.
    /// </summary>
    Task<object?> ResolvePublishedAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>external.vocabulary.schemes</c> /
    /// <c>published.vocabulary.schemes</c> /
    /// <c>published.release.vocabulary.schemes</c> &mdash; schemes list
    /// keyed by public id. Returns the <c>{items, total}</c> envelope.
    /// </summary>
    Task<object?> ListSchemesPublishedAsync(InternalRequest request, CancellationToken cancellationToken);
}