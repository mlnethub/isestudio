using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Ontology;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the six <c>external.*</c> dispatcher arms
/// (11/13 slice). Unpacks the <see cref="InternalRequest"/> envelope
/// (public_id + query strings), delegates to
/// <see cref="ExternalApiService"/> /
/// <see cref="ExternalOntologyService"/>, and returns the wire DTO or
/// <c>null</c> for the dispatcher's empty-envelope fallbacks.
/// </summary>
public sealed class ExternalApplicationService : IExternalApplicationService
{
    private readonly ExternalApiService _api;
    private readonly ExternalOntologyService _ontology;

    public ExternalApplicationService(
        ExternalApiService api,
        ExternalOntologyService ontology)
    {
        _api = api;
        _ontology = ontology;
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
}