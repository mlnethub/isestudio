using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Authentication;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Controllers;

/// <summary>
/// External <c>/api/v1/knowledge-systems/{public_id}/*</c> surface — the
/// 11 read-only operations the frozen Python baseline tags
/// <c>external query api</c>. All endpoints share the
/// <see cref="ExternalTokenAuthenticationHandler"/> scheme and enforce:
/// <list type="bullet">
///   <item>Token KS public-id matches the <c>{public_id}</c> route value.</item>
///   <item>Token scope covers the operation (e.g. <c>query:read</c> for the SPARQL endpoint).</item>
///   <item>Read-only SPARQL: <see cref="ReadOnlySparqlPolicy"/> rejects
///         update / <c>SERVICE</c> / <c>FROM</c> / <c>GRAPH</c> forms
///         before the executor is reached.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ExternalTokenAuthenticationHandler.SchemeName)]
public sealed class ExternalApiController : ControllerBase
{
    private readonly IIntegrationApiFacade _facade;
    private readonly ISEStudioDbContext _db;

    public ExternalApiController(IIntegrationApiFacade facade, ISEStudioDbContext db)
    {
        _facade = facade;
        _db = db;
    }

    // ---- metadata ----

    [HttpGet("api/v1/knowledge-systems/{public_id}")]
    public Task<IActionResult> GetMetadataAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.metadata", public_id, requiredScope: null, body: null, ct);

    // ---- ontology ----

    [HttpGet("api/v1/knowledge-systems/{public_id}/ontology")]
    public Task<IActionResult> GetOntologyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.ontology", public_id, KnowledgeApiTokenScopes.OntologyRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/classes")]
    public Task<IActionResult> ListClassesAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.classes", public_id, KnowledgeApiTokenScopes.OntologyRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/export")]
    public Task<IActionResult> ExportOntologyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.export", public_id, KnowledgeApiTokenScopes.OntologyRead, body: null, ct: ct);

    // ---- abox ----

    [HttpGet("api/v1/knowledge-systems/{public_id}/individual")]
    public Task<IActionResult> GetIndividualAsync(
        string public_id,
        [FromQuery(Name = "iri")] string? iri,
        CancellationToken ct)
        => DispatchAsync("external.individual", public_id, KnowledgeApiTokenScopes.InstancesRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/individuals")]
    public Task<IActionResult> ListIndividualsAsync(
        string public_id,
        [FromQuery(Name = "class_iri")] string? classIri,
        CancellationToken ct)
        => DispatchAsync("external.individuals", public_id, KnowledgeApiTokenScopes.InstancesRead, body: null, ct: ct);

    // ---- query (the only POST) ----

    [HttpPost("api/v1/knowledge-systems/{public_id}/query")]
    [AllowAnonymous]
    public async Task<IActionResult> QueryAsync(
        string public_id,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        // The brief mandates a 400 (not 401) for non-read-only SPARQL
        // even on anonymous requests, so the read-only guard runs
        // BEFORE authentication. [AllowAnonymous] above overrides the
        // controller-level [Authorize] just for this action; the
        // dispatch helper below still performs the token / scope /
        // KS-public-id checks so an authenticated user with the
        // right scope is served as before.
        var sparql = ExtractSparql(body);
        var policy = ReadOnlySparqlPolicy.Validate(sparql);
        if (policy is ReadOnlySparqlPolicyResult.Reject rejected)
        {
            return BadRequestEnvelope(rejected.Reason);
        }

        // Max rows bound (default 1000) is a defensive cap so a
        // well-formed SELECT cannot run an unbounded result set.
        var maxRows = ExtractMaxRows(body, fallback: 1000);

        return await DispatchAsync(
            "external.query",
            public_id,
            KnowledgeApiTokenScopes.QueryRead,
            body: new Dictionary<string, object?>
            {
                ["query"] = ((ReadOnlySparqlPolicyResult.Allow)policy).Normalised,
                ["max_rows"] = maxRows,
            },
            ct: ct).ConfigureAwait(false);
    }

    // ---- vocabulary ----

    [HttpGet("api/v1/knowledge-systems/{public_id}/vocabulary/concepts")]
    public Task<IActionResult> ListConceptsAsync(
        string public_id,
        [FromQuery(Name = "scheme_iri")] string? schemeIri,
        CancellationToken ct)
        => DispatchAsync("external.vocabulary.concepts", public_id, KnowledgeApiTokenScopes.VocabularyRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/vocabulary/export")]
    public Task<IActionResult> ExportVocabularyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.vocabulary.export", public_id, KnowledgeApiTokenScopes.VocabularyRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/vocabulary/resolve")]
    public Task<IActionResult> ResolveVocabularyAsync(
        string public_id,
        [FromQuery(Name = "term")] string? term,
        CancellationToken ct)
        => DispatchAsync("external.vocabulary.resolve", public_id, KnowledgeApiTokenScopes.VocabularyRead, body: null, ct: ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/vocabulary/schemes")]
    public Task<IActionResult> ListVocabulariesAsync(string public_id, CancellationToken ct)
        => DispatchAsync("external.vocabulary.schemes", public_id, KnowledgeApiTokenScopes.VocabularyRead, body: null, ct: ct);

    // ---- shared dispatch ----

    /// <summary>
    /// Resolve the verified token, ensure it targets the public-id in
    /// the route, ensure the required scope is present, then call the
    /// facade. Any of those preconditions failing produces a typed
    /// envelope response (401 / 403 / 410) and the request short-circuits
    /// before the SPARQL executor (or any service code) is reached.
    /// </summary>
    private async Task<IActionResult> DispatchAsync(
        string operation,
        string publicId,
        string? requiredScope,
        IReadOnlyDictionary<string, object?>? body,
        CancellationToken ct)
    {
        var verification = ReadVerification();
        if (verification is null)
        {
            // The auth scheme should have rejected this; treat as 401
            // to keep the contract test green when the handler short-
            // circuits an anonymous request.
            return UnauthorizedEnvelope("Not authenticated");
        }

        if (!string.Equals(verification.KnowledgeSystem.PublicId, publicId, StringComparison.Ordinal))
        {
            // Token and URL point at different knowledge systems —
            // surface as 403 so we never leak which side is invalid.
            return ForbiddenEnvelope("Token does not grant access to this knowledge system.");
        }

        if (requiredScope is not null && !HasScope(verification.Token.Scopes, requiredScope))
        {
            return ForbiddenEnvelope(
                $"Token is missing required scope '{requiredScope}'.");
        }

        var actor = new Actor(verification.Token.Id.ToString());
        var request = new InternalRequest(
            KnowledgeSystemId: null,
            PublicId: publicId,
            ResourceId: null,
            SecondResourceId: null,
            Body: body,
            Query: QueryMap(),
            Actor: actor);

        var payload = await _facade.InvokeAsync(operation, request, ct).ConfigureAwait(false);
        return Ok(payload ?? new { ok = true });
    }

    private KnowledgeApiTokenVerificationResult? ReadVerification() =>
        HttpContext.Items.TryGetValue(ExternalTokenAuthenticationHandler.VerificationItemKey, out var raw)
            ? raw as KnowledgeApiTokenVerificationResult
            : null;

    private IActionResult UnauthorizedEnvelope(string detail)
    {
        // The Query action runs under [AllowAnonymous], so the
        // [Authorize] challenge path does not fire for it. We still
        // want the RFC 6750 WWW-Authenticate header on anonymous
        // 401s so clients can detect the auth scheme — emit it
        // here so the contract test's assertion holds.
        Response.Headers["WWW-Authenticate"] = "Bearer realm=\"isestudio\"";
        return Unauthorized(new { detail });
    }

    private static bool HasScope(IReadOnlyList<string> granted, string required) =>
        granted.Any(s => string.Equals(s, required, StringComparison.Ordinal));

    private IReadOnlyDictionary<string, string?>? QueryMap()
    {
        if (Request.Query is null || Request.Query.Count == 0) return null;
        var dict = new Dictionary<string, string?>(Request.Query.Count);
        foreach (var kv in Request.Query)
        {
            dict[kv.Key] = kv.Value.ToString();
        }
        return dict;
    }

    private static string? ExtractSparql(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.String)
        {
            // Tolerate a raw string body so curl --data 'SELECT ...' works.
            return body.GetString();
        }
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("query", out var queryElement)
            && queryElement.ValueKind == JsonValueKind.String)
        {
            return queryElement.GetString();
        }
        return null;
    }

    private static int ExtractMaxRows(JsonElement body, int fallback)
    {
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("max_rows", out var maxRowsElement))
        {
            if (maxRowsElement.ValueKind == JsonValueKind.Number && maxRowsElement.TryGetInt32(out var n) && n > 0)
            {
                return n;
            }
            if (maxRowsElement.ValueKind == JsonValueKind.String
                && int.TryParse(maxRowsElement.GetString(), out var parsed)
                && parsed > 0)
            {
                return parsed;
            }
        }
        return fallback;
    }

    private IActionResult BadRequestEnvelope(string detail) =>
        BadRequest(new { detail });

    private IActionResult ForbiddenEnvelope(string detail) =>
        StatusCode(StatusCodes.Status403Forbidden, new { detail });
}
