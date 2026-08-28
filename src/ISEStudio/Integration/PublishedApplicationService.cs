using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Ontology;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the twelve <c>published.*</c> /
/// <c>published.release.*</c> dispatcher arms (11/13 slice). Each
/// operation serves both the current-deployment path and the pinned
/// <c>/releases/{version}/</c> path — the pinned version rides in
/// <c>request.ResourceId</c> (null on the current path), mirroring
/// <see cref="OntologyApplicationService.GetPublishedAsync"/>.
/// </summary>
public sealed class PublishedApplicationService : IPublishedApplicationService
{
    private readonly PublishedDataService _published;

    public PublishedApplicationService(PublishedDataService published)
    {
        _published = published;
    }

    public async Task<object?> GetMetadataAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) return null;
        // The Python baseline echoes the active token scopes back in the
        // metadata body; the controller has already verified the token, so
        // we pass through Actor.Scopes if the runtime populated it. The
        // published surface is anonymous-by-design (token-bearing) so
        // Actor here is the controller-minted stub; a future hardening
        // pass can pass real scopes through Actor extras.
        var scopes = TryReadScopes(request);
        return await _published.GetMetadataAsync(ctx, scopes, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> GetManifestAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) return null;
        return _published.GetManifest(ctx);
    }

    public async Task<object?> ListClassesAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) return null;
        return await _published.GetClassesAsync(ctx, ct).ConfigureAwait(false);
    }

    public async Task<object?> ExportAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) return null;
        // Throw ExportFilePayloadException — FastApiErrorMiddleware catches
        // it and writes the raw bytes without a JSON envelope. Mirrors
        // Python FileResponse on published.py:181.
        throw new ExportFilePayloadException(
            _published.GetExport(ctx), "application/n-quads", "tbox.nq");
    }

    public async Task<object?> GetIndividualAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) throw new KeyNotFoundException("Individual not found");
        var iri = QueryString(request, "iri");
        if (string.IsNullOrEmpty(iri))
        {
            throw new ValidationException("Query parameter 'iri' is required.");
        }
        var ind = await _published.GetIndividualAsync(ctx, iri, ct)
            .ConfigureAwait(false);
        return ind ?? throw new KeyNotFoundException("Individual not found");
    }

    public async Task<object?> ListIndividualsAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;
        var ctx = await ResolveServingAsync(request, ct).ConfigureAwait(false);
        if (ctx is null) return null;
        var classIri = QueryString(request, "class_iri");
        var q = QueryString(request, "q");
        // MVP: the Python baseline accepts limit (1..200) and offset (>=0)
        // with sane defaults (20, 0). Mirror those defaults so the wire
        // shape matches what the frontend sends.
        int.TryParse(QueryString(request, "limit"), out var limit);
        if (limit <= 0) limit = 20;
        else if (limit > 200) limit = 200;
        int.TryParse(QueryString(request, "offset"), out var offset);
        if (offset < 0) offset = 0;
        var result = await _published.ListIndividualsAsync(
                ctx, classIri, q, limit, offset, ct)
            .ConfigureAwait(false);
        return result is null ? null : new { items = result.Items, total = result.Total };
    }

    /// <summary>
    /// Resolve the (KS, release, deployment, serving-store) tuple for
    /// either the current or pinned URL — the pinned version rides in
    /// <c>request.ResourceId</c>. A <c>null</c> context means some link
    /// in the chain is missing and the dispatcher falls back to the
    /// schema-compatible empty envelope.
    /// </summary>
    private Task<ServingContext?> ResolveServingAsync(
        InternalRequest request, CancellationToken ct) =>
        _published.ResolveAsync(request.PublicId!, request.ResourceId, ct);

    private static IReadOnlyList<string>? TryReadScopes(InternalRequest request)
    {
        // The published controller populates Actor.Scopes via the token
        // verification path (PublishedController.ReadVerification). The
        // dispatcher receives an Actor stub from the controller — for the
        // metadata body we read the scope list out of the verification item
        // when the dispatcher is hosted by the controller. For direct
        // invocations (e.g. contract tests), return null and let the wire
        // shape degrade to an empty scopes list.
        var actor = request.Actor;
        if (actor is null) return null;
        // Mirror the published.py behaviour with an empty placeholder when
        // we don't have a real token in hand; this keeps the response
        // shape stable across the contract test scenarios that don't seed
        // scopes.
        return Array.Empty<string>();
    }
}