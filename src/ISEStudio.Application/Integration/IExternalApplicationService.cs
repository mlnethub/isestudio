using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the six <c>external.*</c> dispatcher
/// arms that read through the public-id surface (11/13 slice):
/// <c>external.ontology</c> (via
/// <c>ExternalOntologyService.GetViewAsync</c>) plus the five
/// <c>ExternalApiService</c> reads (metadata / classes / export /
/// individual / individuals). All resolve the KS by
/// <c>request.PublicId</c> — not the internal Guid — because the
/// token actor's id is a token Guid, not a user id.
///
/// <para><c>external.query</c> routes through <see cref="QueryAsync"/> —
/// the application service reaches the read-only
/// <see cref="Sparql.ISparqlQueryExecutor"/> directly, so the
/// dispatcher no longer needs to resolve the facade (the historical
/// facade↔dispatcher mutual reference is gone).</para>
///
/// <para>Returns are <c>object?</c> because
/// <see cref="ExternalApiService.GetMetadataAsync"/> returns an
/// anonymous wire envelope (like the 7/13 extraction slice's
/// Infrastructure-dependent DTOs). Each method returns <c>null</c>
/// when the payload should degrade to the dispatcher's
/// schema-compatible empty envelope; a missing <c>public_id</c>
/// throws <see cref="InvalidOperationException"/> exactly like the
/// pre-split helper did.</para>
/// </summary>
public interface IExternalApplicationService
{
    /// <summary><c>external.ontology</c> — full ontology view for the public_id.</summary>
    Task<object?> GetOntologyAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>external.metadata</c> — KS metadata + stats envelope.</summary>
    Task<object?> GetMetadataAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>external.classes</c> — TBox classes with per-class ABox counts.</summary>
    Task<object?> ListClassesAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>external.export</c> — serialized TBox in the requested <c>fmt</c>.</summary>
    Task<object?> ExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>external.individual</c> — one ABox individual by <c>iri</c>.</summary>
    Task<object?> GetIndividualAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>external.individuals</c> — paginated individual listing.</summary>
    Task<object?> ListIndividualsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>external.query</c> — read-only SPARQL against the public_id
    /// graph. Returns <c>null</c> when the public_id / body / query
    /// text is missing so the dispatcher degrades to its empty query
    /// envelope.
    /// </summary>
    Task<object?> QueryAsync(InternalRequest request, CancellationToken cancellationToken);
}