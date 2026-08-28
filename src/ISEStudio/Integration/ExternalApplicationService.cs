using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Sparql;
using ISEStudio.Ontology;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the seven <c>external.*</c> dispatcher arms
/// (11/13 slice + <c>external.query</c>). Unpacks the
/// <see cref="InternalRequest"/> envelope (public_id + query strings),
/// delegates to <see cref="ExternalApiService"/> /
/// <see cref="ExternalOntologyService"/> (and the read-only
/// <see cref="ISparqlQueryExecutor"/> for the query arm), and returns
/// the wire DTO or <c>null</c> for the dispatcher's empty-envelope
/// fallbacks.
/// </summary>
public sealed class ExternalApplicationService : IExternalApplicationService
{
    private readonly ExternalApiService _api;
    private readonly ExternalOntologyService _ontology;
    private readonly ISparqlQueryExecutor _executor;

    public ExternalApplicationService(
        ExternalApiService api,
        ExternalOntologyService ontology,
        ISparqlQueryExecutor executor)
    {
        _api = api;
        _ontology = ontology;
        _executor = executor;
    }

    public async Task<object?> GetOntologyAsync(
        InternalRequest request, CancellationToken ct)
    {
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.ontology");
        return await _ontology.GetViewAsync(publicId, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> GetMetadataAsync(
        InternalRequest request, CancellationToken ct)
    {
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.metadata");
        return await _api.GetMetadataAsync(publicId, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> ListClassesAsync(
        InternalRequest request, CancellationToken ct)
    {
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.classes");
        return await _api.ListClassesAsync(publicId, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> ExportAsync(
        InternalRequest request, CancellationToken ct)
    {
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.export");
        var fmt = QueryString(request, "fmt") ?? "turtle";
        // Parse inside the async path so an unsupported fmt throws
        // ValidationException from here (→ 400), matching the
        // pre-split helper. The parser was promoted to
        // OntologyApplicationService.ParseExportFormat in the 6/13
        // slice so both export arms share one set of rules.
        var format = OntologyApplicationService.ParseExportFormat(fmt);
        return await _api.ExportAsync(publicId, format, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> GetIndividualAsync(
        InternalRequest request, CancellationToken ct)
    {
        var iri = QueryString(request, "iri");
        if (string.IsNullOrEmpty(iri)) return null;
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.individual");
        return await _api.GetIndividualAsync(publicId, iri, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> ListIndividualsAsync(
        InternalRequest request, CancellationToken ct)
    {
        var publicId = request.PublicId
            ?? throw new InvalidOperationException("publicId required for external.individuals");
        var classIri = QueryString(request, "class_iri");
        var q = QueryString(request, "q");
        var limit = QueryInt(request, "limit", 20);
        var offset = QueryInt(request, "offset", 0);
        return await _api.ListIndividualsAsync(
                publicId, classIri, q, limit, offset, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<object?> QueryAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.PublicId is null || request.Body is null)
        {
            // Controller-level validation has already run; a null body is
            // only reachable when a caller wires the operation without
            // going through ExternalApiController. Degrade to the
            // dispatcher's empty query envelope.
            return null;
        }
        var sparql = request.Body.TryGetValue("query", out var queryObj) ? queryObj as string : null;
        if (string.IsNullOrWhiteSpace(sparql))
        {
            return null;
        }
        var maxRows = request.Body.TryGetValue("max_rows", out var maxObj) && maxObj is int maxInt
            ? maxInt
            : 1000;
        var token = new TokenPrincipal(
            TokenId: request.Actor.UserId,
            KnowledgeSystemPublicId: request.PublicId,
            Scopes: Array.Empty<string>());
        // Same cap the typed facade applies for the MCP path, so both
        // surfaces keep identical bounds.
        return await _executor.ExecuteAsync(
                request.PublicId, sparql, Math.Clamp(maxRows, 1, 10_000), token, ct)
            .ConfigureAwait(false);
    }
}