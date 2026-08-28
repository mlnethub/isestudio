using ISEStudio.Application.Documents;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Documents;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Default in-process implementation of <see cref="IDocumentApplicationService"/>.
/// Each method unpacks one <see cref="InternalRequest"/> envelope
/// (path / query / body / actor) and delegates to the underlying
/// <see cref="DocumentService"/>. The transport-level fallback envelopes
/// (<c>EmptyDocument()</c>, <c>EmptyContribution()</c>, <c>EmptyImpact()</c>,
/// <c>EmptyParseResponse()</c>, <c>EmptyParseBatchResponse()</c>,
/// inline <c>{items:[], total:0L, folders:[]}</c>, inline
/// <c>{ok:false}</c>) stay in the dispatcher to keep each helper a
/// one-line delegate, matching the abox + conflicts slice decisions in
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §2.5.
/// <para>
/// <b>Important non-goals.</b> <c>documents.upload</c> is handled
/// directly by <c>DocumentsController</c> because its body is
/// <c>multipart/form-data</c>; the dispatcher's
/// <c>documents.upload</c> arm remains a defensive
/// <c>NotSupportedException</c> guard, not a service call. This
/// application service therefore exposes no
/// <c>UploadAsync</c> method.
/// </para>
/// </summary>
public sealed class DocumentApplicationService : IDocumentApplicationService
{
    private readonly DocumentService _documents;

    public DocumentApplicationService(DocumentService documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
    }

    // ----------------------------------------------------------------------
    // IDocumentApplicationService
    // ----------------------------------------------------------------------

    public Task<IReadOnlyList<DocumentOut>?> ListAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<IReadOnlyList<DocumentOut>?>(null);
        }
        return _documents.ListAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }

    public Task<DocumentListResponse?> ListPageAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<DocumentListResponse?>(null);
        }
        var folder = QueryString(request, "folder");
        var q = QueryString(request, "q");
        var status = QueryString(request, "status");
        var limit = QueryInt(request, "limit", 50);
        var offset = QueryInt(request, "offset", 0);
        return _documents.ListPageAsync(
            request.KnowledgeSystemGuid.Value, folder, q, status,
            limit, offset, request.Actor, cancellationToken);
    }

    public Task<DocumentOut?> GetAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<DocumentOut?>(null);
        }
        return _documents.GetAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken);
    }

    public Task<DocumentOut?> MoveAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<DocumentOut?>(null);
        }
        var body = DeserializeBody<MoveRequest>(request);
        // Mirrors the dispatcher's old behaviour: a missing body throws
        // InvalidOperationException (the dispatcher arm relied on the
        // thrown exception bubbling up to FastApiErrorMiddleware → HTTP 400).
        if (body is null)
        {
            throw new InvalidOperationException(
                "Request body is required for documents.move.");
        }
        return _documents.MoveAsync(
            request.KnowledgeSystemGuid.Value, docId, body, request.Actor, cancellationToken);
    }

    public Task<IReadOnlyList<ChunkOut>?> ListChunksAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<IReadOnlyList<ChunkOut>?>(null);
        }
        return _documents.ListChunksAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken);
    }

    public Task<ContributionOut?> ContributionAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<ContributionOut?>(null);
        }
        return _documents.ContributionAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken);
    }

    public Task<ImpactOut?> ImpactAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<ImpactOut?>(null);
        }
        return _documents.ImpactAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken);
    }

    public async Task<bool?> DeleteAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return null;
        }
        // DocumentService.DeleteAsync returns Task<bool> (non-nullable);
        // re-wrap in Task<bool?> so the dispatcher can distinguish
        // "service-missing / missing resource id" (null → {ok:false}) from
        // "deleted successfully" (true) / "delete failed" (false).
        return await _documents.DeleteAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ParseResponse?> ParseAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var docId))
        {
            return Task.FromResult<ParseResponse?>(null);
        }
        return _documents.ParseAsync(
            request.KnowledgeSystemGuid.Value, docId, request.Actor, cancellationToken);
    }

    public async Task<ParseBatchResponse> ParseBatchAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return EmptyParseBatchResponse();
        }
        var body = DeserializeBody<ParseBatchIn>(request);
        // Mirrors the dispatcher's old behaviour: a missing body throws
        // InvalidOperationException → FastApiErrorMiddleware → HTTP 400.
        if (body is null)
        {
            throw new InvalidOperationException(
                "Request body is required for documents.parse_batch.");
        }
        // DocumentService.ParseBatchAsync returns Task<ParseBatchResponse?>
        // (nullable); the wrapper normalises to Task<ParseBatchResponse>
        // by falling back to the empty envelope when the service returns
        // null (the OpenAPI wire shape requires a non-null body).
        return await _documents.ParseBatchAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken)
            .ConfigureAwait(false) ?? EmptyParseBatchResponse();
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static ParseBatchResponse EmptyParseBatchResponse() =>
        new(Array.Empty<ParseResponse>(), 0, 0, 0);
}