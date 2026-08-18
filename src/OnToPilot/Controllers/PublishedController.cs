using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Controllers;

/// <summary>
/// Published release surface — the 24 read-only operations the frozen
/// Python baseline tags <c>published release api</c>. Routes follow the
/// shape:
/// <list type="bullet">
///   <item><c>GET /api/v1/knowledge-systems/{public_id}/published/*</c> &mdash; the <em>current</em> pinned release; <c>Cache-Control: private, no-cache</c>.</item>
///   <item><c>GET /api/v1/knowledge-systems/{public_id}/releases/{version}/*</c> &mdash; a <em>specific</em> version; <c>Cache-Control: private, max-age=31536000, immutable</c>.</item>
/// </list>
/// Both surfaces set <c>X-OntoPilot-Release</c> + a quoted
/// <c>ETag</c> derived from the release manifest so HTTP caches can
/// short-circuit on repeat reads. The Python backend uses the same
/// shape &mdash; preserving it keeps the FastAPI contract intact.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ExternalTokenAuthenticationHandler.SchemeName)]
public sealed class PublishedController : ControllerBase
{
    private readonly IIntegrationApiFacade _facade;
    private readonly OnToPilotDbContext _db;

    public PublishedController(IIntegrationApiFacade facade, OnToPilotDbContext db)
    {
        _facade = facade;
        _db = db;
    }

    // ---- current release (pinned via URL = false) ----

    [HttpGet("api/v1/knowledge-systems/{public_id}/published")]
    public Task<IActionResult> GetMetadataAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.metadata", public_id, pinnedVersion: null, requiredScope: null, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/manifest")]
    public Task<IActionResult> GetManifestAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.manifest", public_id, pinnedVersion: null, requiredScope: null, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/ontology")]
    public Task<IActionResult> GetOntologyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.ontology", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/classes")]
    public Task<IActionResult> ListClassesAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.classes", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/export")]
    public Task<IActionResult> ExportOntologyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.export", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/individual")]
    public Task<IActionResult> GetIndividualAsync(
        string public_id,
        [FromQuery(Name = "iri")] string? iri,
        CancellationToken ct)
        => DispatchAsync("published.individual", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.InstancesRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/individuals")]
    public Task<IActionResult> ListIndividualsAsync(
        string public_id,
        [FromQuery(Name = "class_iri")] string? classIri,
        CancellationToken ct)
        => DispatchAsync("published.individuals", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.InstancesRead, body: null, ct);

    [HttpPost("api/v1/knowledge-systems/{public_id}/published/query")]
    public async Task<IActionResult> QueryAsync(
        string public_id,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var sparql = ExtractSparql(body);
        var policy = ReadOnlySparqlPolicy.Validate(sparql);
        if (policy is ReadOnlySparqlPolicyResult.Reject rejected)
        {
            return BadRequestEnvelope(rejected.Reason);
        }
        var maxRows = ExtractMaxRows(body, fallback: 1000);

        return await DispatchAsync(
            "published.query",
            public_id,
            pinnedVersion: null,
            requiredScope: KnowledgeApiTokenScopes.QueryRead,
            body: new Dictionary<string, object?>
            {
                ["query"] = ((ReadOnlySparqlPolicyResult.Allow)policy).Normalised,
                ["max_rows"] = maxRows,
            },
            ct: ct).ConfigureAwait(false);
    }

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/vocabulary/concepts")]
    public Task<IActionResult> ListConceptsAsync(
        string public_id,
        [FromQuery(Name = "scheme_iri")] string? schemeIri,
        CancellationToken ct)
        => DispatchAsync("published.vocabulary.concepts", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/vocabulary/export")]
    public Task<IActionResult> ExportVocabularyAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.vocabulary.export", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/vocabulary/resolve")]
    public Task<IActionResult> ResolveVocabularyAsync(
        string public_id,
        [FromQuery(Name = "term")] string? term,
        CancellationToken ct)
        => DispatchAsync("published.vocabulary.resolve", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/published/vocabulary/schemes")]
    public Task<IActionResult> ListVocabulariesAsync(string public_id, CancellationToken ct)
        => DispatchAsync("published.vocabulary.schemes", public_id, pinnedVersion: null, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    // ---- pinned release (version locked in URL) ----

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}")]
    public Task<IActionResult> GetReleaseMetadataAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release", public_id, pinnedVersion: version, requiredScope: null, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/manifest")]
    public Task<IActionResult> GetReleaseManifestAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.manifest", public_id, pinnedVersion: version, requiredScope: null, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/ontology")]
    public Task<IActionResult> GetReleaseOntologyAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.ontology", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/classes")]
    public Task<IActionResult> ListReleaseClassesAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.classes", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/export")]
    public Task<IActionResult> ExportReleaseAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.export", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.OntologyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/individual")]
    public Task<IActionResult> GetReleaseIndividualAsync(
        string public_id,
        string version,
        [FromQuery(Name = "iri")] string? iri,
        CancellationToken ct)
        => DispatchAsync("published.release.individual", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.InstancesRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/individuals")]
    public Task<IActionResult> ListReleaseIndividualsAsync(
        string public_id,
        string version,
        [FromQuery(Name = "class_iri")] string? classIri,
        CancellationToken ct)
        => DispatchAsync("published.release.individuals", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.InstancesRead, body: null, ct);

    [HttpPost("api/v1/knowledge-systems/{public_id}/releases/{version}/query")]
    public async Task<IActionResult> ReleaseQueryAsync(
        string public_id,
        string version,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var sparql = ExtractSparql(body);
        var policy = ReadOnlySparqlPolicy.Validate(sparql);
        if (policy is ReadOnlySparqlPolicyResult.Reject rejected)
        {
            return BadRequestEnvelope(rejected.Reason);
        }
        var maxRows = ExtractMaxRows(body, fallback: 1000);

        return await DispatchAsync(
            "published.release.query",
            public_id,
            pinnedVersion: version,
            requiredScope: KnowledgeApiTokenScopes.QueryRead,
            body: new Dictionary<string, object?>
            {
                ["query"] = ((ReadOnlySparqlPolicyResult.Allow)policy).Normalised,
                ["max_rows"] = maxRows,
            },
            ct: ct).ConfigureAwait(false);
    }

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/vocabulary/concepts")]
    public Task<IActionResult> ListReleaseConceptsAsync(
        string public_id,
        string version,
        [FromQuery(Name = "scheme_iri")] string? schemeIri,
        CancellationToken ct)
        => DispatchAsync("published.release.vocabulary.concepts", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/vocabulary/export")]
    public Task<IActionResult> ExportReleaseVocabularyAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.vocabulary.export", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/vocabulary/resolve")]
    public Task<IActionResult> ResolveReleaseVocabularyAsync(
        string public_id,
        string version,
        [FromQuery(Name = "term")] string? term,
        CancellationToken ct)
        => DispatchAsync("published.release.vocabulary.resolve", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    [HttpGet("api/v1/knowledge-systems/{public_id}/releases/{version}/vocabulary/schemes")]
    public Task<IActionResult> ListReleaseVocabulariesAsync(string public_id, string version, CancellationToken ct)
        => DispatchAsync("published.release.vocabulary.schemes", public_id, pinnedVersion: version, requiredScope: KnowledgeApiTokenScopes.VocabularyRead, body: null, ct);

    // ---- shared dispatch ----

    /// <summary>
    /// Resolve the verified token, ensure it targets the public-id in
    /// the route, ensure the required scope is present, resolve the
    /// bound release (current or pinned), short-circuit on deployment
    /// lifecycle (503 / 410), stamp cache headers, then call the
    /// facade. The preconditions that fire before the dispatcher are
    /// what the brief's "401 / 403 / 503 + Retry-After / 410" table
    /// maps to.
    /// </summary>
    private async Task<IActionResult> DispatchAsync(
        string operation,
        string publicId,
        string? pinnedVersion,
        string? requiredScope,
        IReadOnlyDictionary<string, object?>? body,
        CancellationToken ct)
    {
        var verification = ReadVerification();
        if (verification is null)
        {
            return UnauthorizedEnvelope("Not authenticated");
        }

        if (!string.Equals(verification.KnowledgeSystem.PublicId, publicId, StringComparison.Ordinal))
        {
            return ForbiddenEnvelope("Token does not grant access to this knowledge system.");
        }

        if (requiredScope is not null && !HasScope(verification.Token.Scopes, requiredScope))
        {
            return ForbiddenEnvelope(
                $"Token is missing required scope '{requiredScope}'.");
        }

        // Resolve the release the caller asked for. A pinned URL means
        // a specific version; a /published/* URL means the current
        // version (whichever deployment is active for this KS).
        var releaseResolution = await ResolveReleaseAsync(
            verification.KnowledgeSystem,
            pinnedVersion,
            ct).ConfigureAwait(false);
        if (releaseResolution.Status == ReleaseResolutionStatus.Provisioning)
        {
            Response.Headers["Retry-After"] = "2";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                detail = "Knowledge system release is still provisioning; retry shortly.",
            });
        }
        if (releaseResolution.Status == ReleaseResolutionStatus.StoppedOrFailed)
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                detail = "Knowledge system release is stopped, failed, or deleted.",
            });
        }
        if (releaseResolution.Status == ReleaseResolutionStatus.NotFound)
        {
            return NotFoundEnvelope(
                pinnedVersion is null
                    ? "Knowledge system has no published release yet."
                    : $"Release '{pinnedVersion}' was not found for this knowledge system.");
        }

        // Stamp the response cache headers. The brief mandates the
        // exact shape:
        //   - pinned  -> "private, max-age=31536000, immutable"
        //   - current -> "private, no-cache"
        // The X-OntoPilot-Release and ETag are set in lockstep so a
        // client that uses the ETag for conditional reads can still
        // surface the resolved version in logs.
        var release = releaseResolution.Release!;
        var pinned = pinnedVersion is not null;
        Response.Headers["X-OntoPilot-Release"] = release.Version;
        Response.Headers.ETag = $"\"{release.ManifestHash}\"";
        Response.Headers.CacheControl = pinned
            ? "private, max-age=31536000, immutable"
            : "private, no-cache";

        var actor = new Actor(verification.Token.Id.ToString());
        var request = new InternalRequest(
            KnowledgeSystemId: null,
            PublicId: publicId,
            ResourceId: pinnedVersion,
            SecondResourceId: null,
            Body: body,
            Query: QueryMap(),
            Actor: actor);

        var payload = await _facade.InvokeAsync(operation, request, ct).ConfigureAwait(false);
        return Ok(payload ?? new { ok = true });
    }

    private enum ReleaseResolutionStatus
    {
        Active,
        Provisioning,
        StoppedOrFailed,
        NotFound,
    }

    private sealed record ReleaseResolution(
        ReleaseResolutionStatus Status,
        ReleaseProjection? Release);

    /// <summary>
    /// Minimal projection of the release + deployment data the cache
    /// headers depend on. We do not need the full TBox/ABox payload
    /// here — the downstream dispatcher / service handles that — just
    /// enough to compute <c>X-OntoPilot-Release</c>, the ETag, and the
    /// provisioning / stopped status branches.
    /// </summary>
    private sealed record ReleaseProjection(string Version, string ManifestHash);

    private async Task<ReleaseResolution> ResolveReleaseAsync(
        KnowledgeSystemEntity knowledgeSystem,
        string? pinnedVersion,
        CancellationToken ct)
    {
        // Pinned lookup: a specific version is requested, so the cache
        // header is the immutable variant. We still need to know
        // whether the deployment lifecycle says we should refuse the
        // request (stopped / failed / deleted).
        if (pinnedVersion is not null)
        {
            var release = await _db.OntologyReleases
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.KnowledgeSystemId == knowledgeSystem.Id && r.Version == pinnedVersion,
                    ct)
                .ConfigureAwait(false);
            if (release is null)
            {
                return new ReleaseResolution(ReleaseResolutionStatus.NotFound, null);
            }
            if (string.Equals(release.Status, "deleted", StringComparison.OrdinalIgnoreCase))
            {
                return new ReleaseResolution(ReleaseResolutionStatus.StoppedOrFailed, null);
            }
            var manifestHash = ComputeManifestHash(release);
            return new ReleaseResolution(
                ReleaseResolutionStatus.Active,
                new ReleaseProjection(release.Version, manifestHash));
        }

        // Current lookup: find the active deployment and walk back to
        // the release row. SQLite does not support DateTimeOffset in
        // ORDER BY; pull the rows client-side and sort in memory.
        var deployment = (await _db.ReleaseDeployments
            .AsNoTracking()
            .Where(d => d.KnowledgeSystemId == knowledgeSystem.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (deployment is null)
        {
            // No deployment yet — the brief treats this as the
            // "provisioning" 503 branch (we don't know the difference
            // between "never deployed" and "deploying right now" from
            // the deployment table alone).
            return new ReleaseResolution(ReleaseResolutionStatus.Provisioning, null);
        }
        if (string.Equals(deployment.Status, "provisioning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deployment.Status, "stopping", StringComparison.OrdinalIgnoreCase))
        {
            return new ReleaseResolution(ReleaseResolutionStatus.Provisioning, null);
        }
        if (string.Equals(deployment.Status, "stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deployment.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new ReleaseResolution(ReleaseResolutionStatus.StoppedOrFailed, null);
        }

        var activeRelease = await _db.OntologyReleases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == deployment.ReleaseId,
                ct)
            .ConfigureAwait(false);
        if (activeRelease is null)
        {
            return new ReleaseResolution(ReleaseResolutionStatus.NotFound, null);
        }

        var hash = ComputeManifestHash(activeRelease);
        return new ReleaseResolution(
            ReleaseResolutionStatus.Active,
            new ReleaseProjection(activeRelease.Version, hash));
    }

    /// <summary>
    /// Stable, deterministic manifest hash for the ETag header. When
    /// the row has a manifest JSON document, hash the canonicalised
    /// bytes; otherwise fall back to the version so the header is
    /// always populated and the test does not have to seed the
    /// manifest column.
    /// </summary>
    private static string ComputeManifestHash(OntologyReleaseEntity release)
    {
        if (release.Manifest is null)
        {
            return $"v{release.Version}";
        }
        var canonical = release.Manifest.RootElement.GetRawText();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private KnowledgeApiTokenVerificationResult? ReadVerification() =>
        HttpContext.Items.TryGetValue(ExternalTokenAuthenticationHandler.VerificationItemKey, out var raw)
            ? raw as KnowledgeApiTokenVerificationResult
            : null;

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

    private IActionResult UnauthorizedEnvelope(string detail) =>
        Unauthorized(new { detail });

    private IActionResult ForbiddenEnvelope(string detail) =>
        StatusCode(StatusCodes.Status403Forbidden, new { detail });

    private IActionResult NotFoundEnvelope(string detail) =>
        NotFound(new { detail });
}
