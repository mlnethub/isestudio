using ISEStudio.Application.Documents;
using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application service for the ten <c>documents.*</c> operations the
/// internal REST contract exposes (the eleventh,
/// <c>documents.upload</c>, is handled directly by
/// <c>DocumentsController</c> because its body is
/// <c>multipart/form-data</c>, which doesn't fit the JSON envelope the
/// facade carries &mdash; the dispatcher's <c>documents.upload</c> arm
/// remains a defensive <c>NotSupportedException</c> guard, matching the
/// pre-split behaviour). Each method unpacks one
/// <see cref="InternalRequest"/> (path / query / body / actor),
/// delegates to the underlying <c>DocumentService</c>, and returns the
/// strongly-typed DTO the dispatcher serialises &mdash; or
/// <c>null</c> when the operation has no body / no resource id.
/// </summary>
public interface IDocumentApplicationService
{
    /// <summary>
    /// <c>documents.list</c> &mdash; every document row the session
    /// user can see in the KS. Returns <c>null</c> when the KS id is
    /// missing (dispatcher maps to <c>Array.Empty&lt;object&gt;()</c>).
    /// </summary>
    Task<IReadOnlyList<DocumentOut>?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.list_page</c> &mdash; paginated view + folder
    /// sidebar. <paramref name="request"/> query carries
    /// <c>folder</c>, <c>q</c>, <c>status</c>, <c>limit</c> + <c>offset</c>.
    /// Returns <c>null</c> when the KS id is missing; dispatcher maps
    /// <c>null</c> + service-returned-<c>null</c> to the
    /// <c>{items:[], total:0L, folders:[]}</c> envelope the OpenAPI
    /// baseline declares.
    /// </summary>
    Task<DocumentListResponse?> ListPageAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.get</c> &mdash; one document by id. Returns
    /// <c>null</c> when the resource id is missing or doesn't parse;
    /// dispatcher maps to <see cref="InternalOperationDispatcher.EmptyDocument"/>.
    /// </summary>
    Task<DocumentOut?> GetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.move</c> &mdash; re-parent one document. Body
    /// deserialised via <see cref="MoveRequest"/>; throws
    /// <see cref="InvalidOperationException"/> when the body is missing
    /// (matches the dispatcher's old behaviour &mdash;
    /// <c>FastApiErrorMiddleware</c> translates it to HTTP 400).
    /// Returns <c>null</c> only when the resource id doesn't parse
    /// (dispatcher maps to <see cref="InternalOperationDispatcher.EmptyDocument"/>).
    /// </summary>
    Task<DocumentOut?> MoveAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.list_chunks</c> &mdash; ordered chunk rows for one
    /// document. Returns <c>null</c> when the resource id is missing
    /// or doesn't parse; dispatcher maps to
    /// <c>Array.Empty&lt;object&gt;()</c>.
    /// </summary>
    Task<IReadOnlyList<ChunkOut>?> ListChunksAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.contribution</c> &mdash; per-document ontology
    /// contribution count bundle. Returns <c>null</c> when the resource
    /// id is missing or doesn't parse; dispatcher maps to
    /// <see cref="InternalOperationDispatcher.EmptyContribution"/>.
    /// </summary>
    Task<ContributionOut?> ContributionAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.impact</c> &mdash; cross-KS impact report for one
    /// document. Returns <c>null</c> when the resource id is missing
    /// or doesn't parse; dispatcher maps to
    /// <see cref="InternalOperationDispatcher.EmptyImpact"/>.
    /// </summary>
    Task<ImpactOut?> ImpactAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.delete</c> &mdash; drop one document. Returns
    /// <c>null</c> only when the resource id is missing or doesn't
    /// parse; dispatcher maps to <c>{ok:false}</c>. <c>DocumentService</c>
    /// itself returns <c>bool</c> (non-nullable) for the success path;
    /// the dispatcher projects the result into the wire shape
    /// <c>{ok:bool}</c>.
    /// </summary>
    Task<bool?> DeleteAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.parse</c> &mdash; kick the parser for one document.
    /// Returns <c>null</c> when the resource id is missing or doesn't
    /// parse; dispatcher maps to
    /// <see cref="InternalOperationDispatcher.EmptyParseResponse"/>.
    /// </summary>
    Task<ParseResponse?> ParseAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>documents.parse_batch</c> &mdash; kick the parser for one or
    /// more documents in a single request. Body deserialised via
    /// <see cref="ParseBatchIn"/>; throws
    /// <see cref="InvalidOperationException"/> when the body is missing
    /// (matches the dispatcher's old behaviour). When the KS id is
    /// missing the service returns an empty
    /// <see cref="ParseBatchResponse"/> (zero counts) rather than
    /// <c>null</c>, because <c>ParseBatchResponse</c> is non-nullable
    /// in the wire contract.
    /// </summary>
    Task<ParseBatchResponse> ParseBatchAsync(InternalRequest request, CancellationToken cancellationToken);
}