using System.Text;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Integration;

/// <summary>
/// Implementation of <see cref="IVocabularyApplicationService"/>.
/// Unpacks each <see cref="InternalRequest"/> (path / query / body /
/// actor), delegates to the underlying domain services
/// (<see cref="VocabularyService"/>, <see cref="VocabularyProposalService"/>,
/// <see cref="TerminologyAgent"/>), and returns the strongly-typed DTO
/// the dispatcher serialises.
///
/// <para>The dispatcher arm layer still owns the schema-compatible empty
/// payload fallback envelopes (<c>EmptyVocabularyResponse()</c> /
/// <c>EmptyVocabularySchemeList()</c> / <c>EmptyScheme()</c> /
/// <c>EmptyConcept()</c> / <c>EmptyProposal()</c> /
/// <c>EmptySyncResponse()</c> / <c>EmptyListResponse()</c> /
/// <c>string.Empty</c>) &mdash; the application service returns
/// <c>null</c> when the KS is missing or the service isn't wired, and the
/// dispatcher substitutes the right envelope. See
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §2.5 for the rationale.</para>
///
/// <para>Cross-surface (external / published / published.release)
/// vocabulary ops resolve the KS by <c>request.PublicId</c> via
/// <see cref="InternalRequestHelpers.ResolveKsByPublicIdAsync"/>;
/// internal vocabulary ops resolve it by
/// <c>request.KnowledgeSystemGuid</c> via
/// <see cref="InternalRequestHelpers.ResolveKsAsync"/>. Both paths
/// share the same <see cref="VocabularyService"/> read methods &mdash;
/// the Reader gate inside the service enforces access.</para>
/// </summary>
public sealed class VocabularyApplicationService : IVocabularyApplicationService
{
    private readonly IServiceProvider _services;
    private readonly VocabularyService _vocabulary;
    private readonly VocabularyProposalService _proposals;
    private readonly TerminologyAgent _terminology;

    public VocabularyApplicationService(
        IServiceProvider services,
        VocabularyService vocabulary,
        VocabularyProposalService proposals,
        TerminologyAgent terminology)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(terminology);
        _services = services;
        _vocabulary = vocabulary;
        _proposals = proposals;
        _terminology = terminology;
    }

    // ----------------------------------------------------------------------
    // Internal vocabulary reads
    // ----------------------------------------------------------------------

    public async Task<SkosView?> GetAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        return await _vocabulary.GetVocabularyAsync(ks, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<object?> ListSchemesAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var view = await _vocabulary.GetVocabularyAsync(ks, request.Actor, ct).ConfigureAwait(false);
        if (view is null) return null;
        return new
        {
            items = view.Schemes,
            total = view.Schemes.Count,
            stats = view.Stats,
        };
    }

    public async Task<object?> ListConceptsAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var schemeIri = InternalRequestHelpers.QueryString(request, "scheme_iri");
        var q = InternalRequestHelpers.QueryString(request, "q");
        var status = InternalRequestHelpers.QueryString(request, "status");
        var mapping = InternalRequestHelpers.QueryString(request, "mapping");
        var origin = InternalRequestHelpers.QueryString(request, "origin");
        var limit = InternalRequestHelpers.QueryInt(request, "limit", 100);
        var offset = InternalRequestHelpers.QueryInt(request, "offset", 0);

        var page = await _vocabulary.ListConceptsAsync(
            ks, schemeIri, q, status, mapping, origin, limit, offset,
            request.Actor, ct).ConfigureAwait(false);
        if (page is null) return null;
        return new { items = page.Items, total = page.Total };
    }

    public async Task<object?> ResolveTermAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var q = InternalRequestHelpers.QueryString(request, "q") ?? string.Empty;
        var language = InternalRequestHelpers.QueryString(request, "language");
        var limit = InternalRequestHelpers.QueryInt(request, "limit", 10);

        var result = await _vocabulary.ResolveTermAsync(ks, q, language, limit, request.Actor, ct)
            .ConfigureAwait(false);
        if (result is null) return null;
        return new { items = result.Value.Items, total = result.Value.Total };
    }

    public async Task<string?> ExportAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var fmt = InternalRequestHelpers.QueryString(request, "fmt") ?? "n-quads";
        var bytes = await _vocabulary.ExportVocabularyAsync(ks, fmt, request.Actor, ct)
            .ConfigureAwait(false);
        if (bytes is null) return null;
        return Encoding.UTF8.GetString(bytes);
    }

    // ----------------------------------------------------------------------
    // Internal vocabulary writes (scheme)
    // ----------------------------------------------------------------------

    public async Task<SkosSchemeView?> CreateSchemeAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var data = InternalRequestHelpers.DeserializeBody<SkosSchemeData>(request)
            ?? throw new InvalidOperationException("Request body is required for vocabulary.create_scheme.");
        return await _vocabulary.CreateSchemeAsync(ks, data, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<SkosSchemeView?> UpdateSchemeAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        var iri = request.ResourceId ?? string.Empty;
        if (ks is null || string.IsNullOrEmpty(iri)) return null;

        var data = InternalRequestHelpers.DeserializeBody<SkosSchemeData>(request)
            ?? throw new InvalidOperationException("Request body is required for vocabulary.update_scheme.");
        return await _vocabulary.UpdateSchemeAsync(ks, iri, data, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<object?> DeleteSchemeAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        var iri = request.ResourceId ?? string.Empty;
        if (ks is null || string.IsNullOrEmpty(iri))
        {
            return new { deleted = (string?)null, removed_triples = 0 };
        }

        var result = await _vocabulary.DeleteSchemeAsync(ks, iri, request.Actor, ct).ConfigureAwait(false);
        if (result is null) return new { deleted = iri, removed_triples = 0 };
        return new
        {
            deleted = result.Value.DeletedIri,
            removed_triples = result.Value.RemovedTriples,
        };
    }

    // ----------------------------------------------------------------------
    // Internal vocabulary writes (concept)
    // ----------------------------------------------------------------------

    public async Task<SkosConceptView?> CreateConceptAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var data = InternalRequestHelpers.DeserializeBody<SkosConceptData>(request)
            ?? throw new InvalidOperationException("Request body is required for vocabulary.create_concept.");
        return await _vocabulary.CreateConceptAsync(ks, data.SchemeIri, data, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<SkosConceptView?> UpdateConceptAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var data = InternalRequestHelpers.DeserializeBody<SkosConceptData>(request)
            ?? throw new InvalidOperationException("Request body is required for vocabulary.update_concept.");
        var iri = !string.IsNullOrEmpty(data.Iri) ? data.Iri : (request.ResourceId ?? string.Empty);
        if (string.IsNullOrEmpty(iri)) return null;

        return await _vocabulary.UpdateConceptAsync(ks, iri, data, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<object?> DeleteConceptAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        var iri = InternalRequestHelpers.ExtractBodyIri(request) ?? request.ResourceId ?? string.Empty;
        if (ks is null || string.IsNullOrEmpty(iri))
        {
            return new { deleted = (string?)null, removed_triples = 0 };
        }

        var result = await _vocabulary.DeleteConceptAsync(ks, iri, request.Actor, ct).ConfigureAwait(false);
        if (result is null) return new { deleted = iri, removed_triples = 0 };
        return new
        {
            deleted = result.Value.DeletedIri,
            removed_triples = result.Value.RemovedTriples,
        };
    }

    // ----------------------------------------------------------------------
    // Internal vocabulary sync
    // ----------------------------------------------------------------------

    public async Task<object?> SyncAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var result = await _vocabulary.SyncAsync(ks, request.Actor, ct).ConfigureAwait(false);
        // TerminologyResult serialises to JSON via the public property names;
        // the dispatcher maps null to EmptySyncResponse().
        return result;
    }

    // ----------------------------------------------------------------------
    // Proposals + suggest
    // ----------------------------------------------------------------------

    public async Task<object?> ListProposalsAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var status = InternalRequestHelpers.QueryString(request, "status");
        var q = InternalRequestHelpers.QueryString(request, "q");
        var limit = InternalRequestHelpers.QueryInt(request, "limit", 100);
        var offset = InternalRequestHelpers.QueryInt(request, "offset", 0);

        var result = await _proposals.ListProposalsAsync(
            ks, status, q, limit, offset, request.Actor, ct).ConfigureAwait(false);
        if (result is null) return null;
        return new { items = result.Value.Items, total = result.Value.Total };
    }

    public async Task<object?> AcceptProposalAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        if (!Guid.TryParse(request.ResourceId, out var proposalId) || proposalId == Guid.Empty)
        {
            return null;
        }

        var body = InternalRequestHelpers.DeserializeLooseBody(request);
        var payload = InternalRequestHelpers.ExtractPayload(body);
        var note = body?["note"]?.ToString() ?? string.Empty;

        var result = await _proposals.AcceptProposalAsync(
            ks, proposalId, payload, note, request.Actor, ct).ConfigureAwait(false);
        if (result is null) return null;
        return new
        {
            proposal = result.Value.Proposal,
            concept = result.Value.Concept,
        };
    }

    public async Task<object?> RejectProposalAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        if (!Guid.TryParse(request.ResourceId, out var proposalId) || proposalId == Guid.Empty)
        {
            return null;
        }

        var body = InternalRequestHelpers.DeserializeLooseBody(request);
        var note = body?["note"]?.ToString() ?? string.Empty;

        var proposal = await _proposals.RejectProposalAsync(
            ks, proposalId, note, request.Actor, ct).ConfigureAwait(false);
        return proposal;
    }

    public async Task<object?> SuggestTermsAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        // NB: keep `Dictionary<string, object?>` (not `DeserializeLooseBody`)
        // so the `chunk_ids` array stays a `JsonElement` and
        // `ExtractChunkIds` can iterate it. `DeserializeLooseBody` would
        // flatten arrays through `JsonElementToObject` (which returns
        // `GetRawText()` for arrays), dropping the list silently.
        var body = InternalRequestHelpers.DeserializeBody<Dictionary<string, object?>>(request);
        var schemeIri = body?["scheme_iri"]?.ToString() ?? string.Empty;
        var model = body?["model"]?.ToString();
        var chunkIds = InternalRequestHelpers.ExtractChunkIds(body);

        var proposals = await _terminology.SuggestAsync(ks, schemeIri, chunkIds, model, ct)
            .ConfigureAwait(false);
        return new { items = proposals, total = proposals.Count };
    }

    // ----------------------------------------------------------------------
    // Cross-surface (publicId-keyed) — shared by external / published /
    // published.release vocabulary routes
    // ----------------------------------------------------------------------

    public async Task<object?> ListConceptsPublishedAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsByPublicIdAsync(
            request.PublicId, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var schemeIri = InternalRequestHelpers.QueryString(request, "scheme_iri");
        var q = InternalRequestHelpers.QueryString(request, "q");
        var status = InternalRequestHelpers.QueryString(request, "status");
        var mapping = InternalRequestHelpers.QueryString(request, "mapping");
        var origin = InternalRequestHelpers.QueryString(request, "origin");
        var limit = InternalRequestHelpers.QueryInt(request, "limit", 100);
        var offset = InternalRequestHelpers.QueryInt(request, "offset", 0);

        var page = await _vocabulary.ListConceptsAsync(
            ks, schemeIri, q, status, mapping, origin, limit, offset,
            request.Actor, ct).ConfigureAwait(false);
        if (page is null) return null;
        return new { items = page.Items, total = page.Total };
    }

    public async Task<string?> ExportPublishedAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsByPublicIdAsync(
            request.PublicId, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var fmt = InternalRequestHelpers.QueryString(request, "fmt") ?? "n-quads";
        var bytes = await _vocabulary.ExportVocabularyAsync(ks, fmt, request.Actor, ct)
            .ConfigureAwait(false);
        if (bytes is null) return null;
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<object?> ResolvePublishedAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsByPublicIdAsync(
            request.PublicId, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var q = InternalRequestHelpers.QueryString(request, "q") ?? string.Empty;
        var language = InternalRequestHelpers.QueryString(request, "language");
        var limit = InternalRequestHelpers.QueryInt(request, "limit", 10);

        var result = await _vocabulary.ResolveTermAsync(ks, q, language, limit, request.Actor, ct)
            .ConfigureAwait(false);
        if (result is null) return null;
        return new { items = result.Value.Items, total = result.Value.Total };
    }

    public async Task<object?> ListSchemesPublishedAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsByPublicIdAsync(
            request.PublicId, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var schemes = await _vocabulary.ListSchemesAsync(ks, request.Actor, ct).ConfigureAwait(false);
        if (schemes is null) return null;
        return new { items = schemes, total = schemes.Count };
    }
}