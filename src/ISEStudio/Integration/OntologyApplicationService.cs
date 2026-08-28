using System.Text;
using System.Text.Json;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Ontology;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Ontology;
using Microsoft.EntityFrameworkCore;
using Oxigraph;

namespace ISEStudio.Integration;

/// <summary>
/// Implementation of <see cref="IOntologyApplicationService"/>.
/// Unpacks each <see cref="InternalRequest"/> (path / query / body /
/// actor), delegates to the underlying domain services
/// (<see cref="OntologyService"/>,
/// <see cref="OntologyProvenanceService"/>,
/// <see cref="RdfExportService"/>,
/// <see cref="PublishedOntologyService"/>), and returns the
/// strongly-typed DTO the dispatcher serialises.
///
/// <para>Role gates, the extraction guard, and audit diffs all live
/// inside the underlying services &mdash; the application service is a
/// thin envelope-unpacking shim. The dispatcher arm layer still owns
/// the schema-compatible empty payload fallbacks (see the spec §3.3).</para>
///
/// <para>Cross-surface (<c>published.ontology</c> /
/// <c>published.release.ontology</c>) reads resolve the KS via
/// <c>request.PublicId</c> (NOT the internal Guid — external /
/// published callers never see the internal id) and forward either a
/// pinned <c>version</c> string from <c>request.ResourceId</c> or
/// <c>null</c> when no version is bound, in which case the latest
/// <c>ReleaseDeployment</c> row picks the active release.</para>
/// </summary>
public sealed class OntologyApplicationService : IOntologyApplicationService
{
    private readonly IServiceProvider _services;
    private readonly OntologyService _ontology;
    private readonly OntologyProvenanceService _provenance;
    private readonly RdfExportService _rdfExport;
    private readonly PublishedOntologyService _publishedOntology;

    public OntologyApplicationService(
        IServiceProvider services,
        OntologyService ontology,
        OntologyProvenanceService provenance,
        RdfExportService rdfExport,
        PublishedOntologyService publishedOntology)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(rdfExport);
        ArgumentNullException.ThrowIfNull(publishedOntology);
        _services = services;
        _ontology = ontology;
        _provenance = provenance;
        _rdfExport = rdfExport;
        _publishedOntology = publishedOntology;
    }

    // ----------------------------------------------------------------------
    // Internal workspace ontology reads / writes
    // ----------------------------------------------------------------------

    public async Task<OntologyResponse?> GetAsync(InternalRequest request, CancellationToken ct)
    {
        var ksId = request.KnowledgeSystemGuid;
        if (ksId is null) return null;
        return await _ontology.GetViewAsync(ksId.Value, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<OntologyEditResult?> EditAsync(InternalRequest request, CancellationToken ct)
    {
        var ksId = request.KnowledgeSystemGuid;
        if (ksId is null) return null;
        var op = DeserializeOntologyEditBody(request)
            ?? throw new InvalidOperationException("Request body is required for ontology.edit.");
        return await _ontology.EditAsync(ksId.Value, op, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<string?> ExportAsync(InternalRequest request, CancellationToken ct)
    {
        var ks = await InternalRequestHelpers.ResolveKsAsync(
            request.KnowledgeSystemGuid, _services, ct).ConfigureAwait(false);
        if (ks is null) return null;
        var fmt = InternalRequestHelpers.QueryString(request, "fmt") ?? "turtle";
        var format = ParseExportFormat(fmt);
        var bytes = await _rdfExport.ExportAsync(
            KsContext.FromEntity(ks), RdfLayer.TBox, format, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<OntologyEditResult?> ResetAsync(InternalRequest request, CancellationToken ct)
    {
        var ksId = request.KnowledgeSystemGuid;
        if (ksId is null) return null;
        return await _ontology.ResetAsync(ksId.Value, request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProvenanceGroupOut>?> ProvenanceAsync(
        InternalRequest request, CancellationToken ct)
    {
        var ksId = request.KnowledgeSystemGuid;
        if (ksId is null) return null;
        return await _provenance.GetProvenanceAsync(ksId.Value, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SourceOut>?> SourcesAsync(
        InternalRequest request, CancellationToken ct)
    {
        var ksId = request.KnowledgeSystemGuid;
        if (ksId is null) return null;
        return await _provenance.ListSourcesAsync(ksId.Value, request.Actor, ct)
            .ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // Cross-surface (publicId-keyed) — shared by published.ontology +
    // published.release.ontology
    // ----------------------------------------------------------------------

    public async Task<OntologyResponse?> GetPublishedAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PublicId)) return null;

        var effectiveVersion = request.ResourceId;
        if (string.IsNullOrEmpty(effectiveVersion))
        {
            var db = _services.GetService(typeof(ISEStudioDbContext)) as ISEStudioDbContext;
            if (db is null) return null;

            var ks = await db.KnowledgeSystems.AsNoTracking()
                .FirstOrDefaultAsync(k => k.PublicId == request.PublicId, ct)
                .ConfigureAwait(false);
            if (ks is null) return null;

            // SQLite does not support DateTimeOffset in ORDER BY — pull
            // the rows client-side and sort in memory, mirroring the
            // controller-side ResolveReleaseAsync pattern.
            var deployment = (await db.ReleaseDeployments.AsNoTracking()
                .Where(d => d.KnowledgeSystemId == ks.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();
            if (deployment is null) return null;

            var release = await db.OntologyReleases.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == deployment.ReleaseId, ct)
                .ConfigureAwait(false);
            if (release is null) return null;

            effectiveVersion = release.Version;
        }

        return await _publishedOntology.GetViewAsync(
            request.PublicId, effectiveVersion, request.Actor, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // Local helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Pull the edit body as a loose dictionary so the JSON the
    /// frontend sends (with no declared C# type) lands on the service
    /// call as the same shape. The
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> naming policy
    /// configured in <c>Program.cs</c> means both <c>"op"</c> /
    /// <c>"label"</c> / <c>"comment"</c> properties are accepted
    /// without an explicit <c>[JsonPropertyName]</c> per field.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? DeserializeOntologyEditBody(
        InternalRequest request)
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null) return null;
        if (raw is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Request body must be a JSON object for ontology.edit.");
            }
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
            return dict;
        }
        if (raw is IReadOnlyDictionary<string, object?> alreadyDict)
        {
            return alreadyDict;
        }
        return null;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (object)el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    /// <summary>
    /// Map the wire <c>fmt</c> query string onto the typed
    /// <see cref="RdfFormat"/> enum. Unknown formats raise
    /// <see cref="ISEStudio.Api.ValidationException"/> → HTTP 400, matching
    /// the Python <c>HTTPException(400, "Unsupported format")</c>
    /// contract. Lives on the application service so
    /// <c>external.export</c> (which still goes through the dispatcher in
    /// 11/13) can share the same parsing rules via a future call into
    /// the application service.
    /// </summary>
    internal static RdfFormat ParseExportFormat(string fmt)
    {
        var normalized = fmt.Trim().ToLowerInvariant();
        return normalized switch
        {
            "turtle" or "ttl" => RdfFormat.Turtle,
            "ntriples" or "nt" or "n-triples" => RdfFormat.NTriples,
            "nquads" or "n-quads" or "nq" => RdfFormat.NQuads,
            "trig" => RdfFormat.TriG,
            "rdfxml" or "rdf/xml" or "xml" or "rdf" => RdfFormat.RdfXml,
            "jsonld" or "json-ld" or "json" => RdfFormat.JsonLd,
            _ => throw new ISEStudio.Api.ValidationException(
                $"Unsupported export format: {fmt}. Use turtle, ntriples, nquads, trig, rdfxml, or jsonld."),
        };
    }
}