using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the twelve <c>published.*</c> /
/// <c>published.release.*</c> dispatcher arms (11/13 slice):
/// metadata / manifest / classes / export / individual / individuals,
/// each reachable on the current-deployment path and the pinned
/// <c>/releases/{version}/</c> path. The two paths share one method
/// per operation — the pinned version is carried in
/// <c>request.ResourceId</c> (null on the current path), mirroring
/// <see cref="IOntologyApplicationService.GetPublishedAsync"/>.
///
/// <para>Returns are <c>object?</c> because the metadata envelope is
/// anonymous and the serving context depends on Infrastructure
/// entities. A <c>null</c> return degrades to the dispatcher's
/// schema-compatible empty envelope per arm.</para>
///
/// <para><c>published.query</c> / <c>published.release.query</c> route
/// through <see cref="QueryAsync"/> — both arms share the read-only
/// <see cref="Sparql.ISparqlQueryExecutor"/> directly, so the
/// dispatcher no longer needs to resolve the facade (the historical
/// facade↔dispatcher mutual reference is gone).</para>
/// </summary>
public interface IPublishedApplicationService
{
    /// <summary>metadata — release/deployment metadata for the public_id.</summary>
    Task<object?> GetMetadataAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>manifest — release manifest for the public_id.</summary>
    Task<object?> GetManifestAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>classes — published TBox classes.</summary>
    Task<object?> ListClassesAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// export — raw TBox N-Quads. On a resolved serving context this
    /// throws <c>ExportFilePayloadException</c> (the middleware writes
    /// the raw bytes); on a missing context it returns <c>null</c> so
    /// the dispatcher falls back to the empty byte payload.
    /// </summary>
    Task<object?> ExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// individual — one ABox individual by <c>iri</c>. Throws
    /// <see cref="KeyNotFoundException"/> when the individual (or the
    /// serving context) is missing and
    /// <see cref="Api.ValidationException"/> when <c>iri</c> is absent —
    /// matching the pre-split helper's throw semantics.
    /// </summary>
    Task<object?> GetIndividualAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>individuals — paginated published ABox listing.</summary>
    Task<object?> ListIndividualsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// query — read-only SPARQL against the public_id graph. Shared by
    /// <c>published.query</c> (current deployment) and
    /// <c>published.release.query</c> (pinned version). Returns
    /// <c>null</c> when the public_id / body / query text is missing so
    /// the dispatcher degrades to its empty query envelope.
    /// </summary>
    Task<object?> QueryAsync(InternalRequest request, CancellationToken cancellationToken);
}