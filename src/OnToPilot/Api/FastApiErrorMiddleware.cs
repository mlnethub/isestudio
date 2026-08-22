using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OnToPilot.Ontology;
using OnToPilot.Serialization;

namespace OnToPilot.Api;

/// <summary>
/// Thrown by service-layer validation guards (required-field checks,
/// range / enum constraints, length caps). Caught by
/// <see cref="FastApiErrorMiddleware"/> and translated to HTTP 400 with
/// the standard FastAPI <c>{"detail": "..."}</c> envelope.
///
/// <para>Distinct from <see cref="SkosValidationException"/>: SKOS payload
/// validation emits 422 (semantically "well-formed but semantically
/// invalid") to mirror Pydantic's <c>ValidationError</c>; general request
/// validation emits 400 ("the request itself is malformed").</para>
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
    public ValidationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Translates every error response into FastAPI's <c>{"detail": ...}</c>
/// envelope. The default ASP.NET Core pipeline exposes
/// <c>application/problem+json</c> for unhandled routes, model validation
/// failures, and exception handlers; the Python backend emits <c>{"detail":
/// "..."}</c> instead and our existing client tooling depends on that shape.
/// </summary>
/// <remarks>
/// <para>The middleware handles four failure shapes:</para>
/// <list type="number">
///   <item>Unhandled exceptions → HTTP 500 <c>{"detail": "Internal server error"}</c> (no stack trace is ever leaked).</item>
///   <item><see cref="GraphWriteConflictException"/> → HTTP 409 with the
///         structured <c>{"detail": { "error": "...", "job_id": "..." }}</c>
///         envelope the brief's "抽取进行中的修改返回 409" requirement
///         mandates (an extraction is in progress, a mutation tried to land).</item>
///   <item><see cref="ValidationException"/> → HTTP 400 with the plain-string
///         <c>{"detail": "..."}</c> envelope (request-input problems:
///         missing required field, length cap, kind / range enum, etc.).</item>
///   <item>Empty 4xx responses (e.g. unmatched routes) → envelope with a status-appropriate message.</item>
/// </list>
/// <para>Per-endpoint 401/403/404 details are emitted by the controller /
/// <see cref="Authentication.SessionAuthenticationHandler"/> when the
/// reason is known; this middleware only fills the holes.</para>
/// </remarks>
public sealed class FastApiErrorMiddleware
{
    // Mirror the Program.cs resolver chain: source-generated context first,
    // reflection fallback for the structured ConflictDetail record and any
    // other anonymous payload the middlewares still hand-roll.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            OnToPilotJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<FastApiErrorMiddleware> _logger;

    public FastApiErrorMiddleware(RequestDelegate next, ILogger<FastApiErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (GraphWriteConflictException ex)
        {
            // Surface as HTTP 409 with the structured envelope the brief
            // mandates ("抽取进行中的修改返回 409"). The detail field carries
            // both the human reason and the offending job id so clients
            // can poll /api/knowledge/{ks_id}/jobs/{job_id} for the
            // extraction that blocked the write.
            await WriteEnvelopeAsync(
                context,
                StatusCodes.Status409Conflict,
                new ConflictDetail(ex.Message, ex.JobId)).ConfigureAwait(false);
            return;
        }
        catch (ResourceInUseException ex)
        {
            // Distinct from GraphWriteConflictException: a delete was
            // refused because some other row still references the target
            // (e.g. a knowledge system pointing at the provider row).
            // The wire shape is the plain-string {"detail": "..."} the
            // Python FastAPI backend emits in the same scenario.
            await WriteEnvelopeAsync(context, StatusCodes.Status409Conflict, ex.Message)
                .ConfigureAwait(false);
            return;
        }
        catch (KeyNotFoundException ex)
        {
            await WriteEnvelopeAsync(context, StatusCodes.Status404NotFound,
                new FastApiError(ex.Message)).ConfigureAwait(false);
            return;
        }
        catch (SkosValidationException ex)
        {
            // SKOS vocabulary validation failures (missing scheme_iri on a
            // create proposal, unknown pref-label, duplicate language,
            // etc.) are client-input problems, not server faults. Surface
            // them as 422 Unprocessable Entity so the caller can react
            // rather than treat the response as an opaque 5xx. The wire
            // shape is the plain-string {"detail": "..."} envelope the
            // Python FastAPI backend emits for Pydantic ValidationError.
            _logger.LogInformation(
                "SKOS payload validation refused: {Reason}", ex.Message);
            await WriteEnvelopeAsync(context, StatusCodes.Status422UnprocessableEntity, ex.Message)
                .ConfigureAwait(false);
            return;
        }
        catch (RdfImportException ex)
        {
            // RDF import failures (unsupported format, empty file,
            // exceeded max bytes / triples, unsupported target /
            // strategy value). Treat as 400 with the plain-string
            // {"detail": "..."} envelope so the front-end toast shows
            // the human-readable reason verbatim — matches the Python
            // backend's rdf_import HTTPException → 400 mapping.
            _logger.LogInformation(
                "RDF import refused: {Reason}", ex.Message);
            await WriteEnvelopeAsync(context, StatusCodes.Status400BadRequest, ex.Message)
                .ConfigureAwait(false);
            return;
        }
        catch (ValidationException ex)
        {
            // Service-layer validation guards (required-field checks,
            // length caps, range / kind enum) emit this. Surface as 400
            // Bad Request with the plain-string {"detail": "..."}
            // envelope so contract tests that POST an empty body see a
            // 4xx (client error) instead of a 5xx (server fault). The
            // message is intentional and surfaced verbatim through the
            // frontend toast, so keep it human-readable.
            _logger.LogInformation(
                "Request validation refused: {Reason}", ex.Message);
            await WriteEnvelopeAsync(context, StatusCodes.Status400BadRequest, ex.Message)
                .ConfigureAwait(false);
            return;
        }
        catch (ExportFilePayloadException ex)
        {
            // Download-style raw response: write Content-Type +
            // Content-Disposition + the bytes verbatim, skip the JSON
            // envelope. Used by releases.download_export_file to mirror
            // Python FileResponse. MUST be caught BEFORE the generic
            // Exception handler so the 500 path doesn't fire on an
            // intentional sentinel.
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ex.MediaType;
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{ex.FileName}\"";
            await context.Response.Body.WriteAsync(ex.Bytes, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in OnToPilot pipeline");
            await WriteEnvelopeAsync(context, StatusCodes.Status500InternalServerError, "Internal server error")
                .ConfigureAwait(false);
            return;
        }

        // If the inner pipeline produced a 4xx/5xx with no body (an
        // unmatched API route, or a misuse middleware that didn't write),
        // fill it with our envelope so clients see the expected shape.
        if (!context.Response.HasStarted
            && context.Response.StatusCode >= 400
            && context.Response.ContentLength is null or 0)
        {
            var detail = DetailFor(context.Response.StatusCode);
            await WriteEnvelopeAsync(context, context.Response.StatusCode, detail)
                .ConfigureAwait(false);
        }
    }

    private static string DetailFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Not authenticated",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        _ => $"HTTP {status}",
    };

    private static async Task WriteEnvelopeAsync(HttpContext context, int statusCode, object detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = JsonSerializer.SerializeToUtf8Bytes(new FastApiError(detail), JsonOptions);
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The structured detail payload for an in-progress-extraction
    /// 409. The shape matches the FastAPI envelope
    /// (<c>{"detail": { "error": "...", "job_id": "..." }}</c>) so
    /// existing tooling keeps working. Property names are explicitly
    /// pinned to snake_case via <see cref="JsonPropertyNameAttribute"/>
    /// so the wire shape stays stable even though the controller-layer
    /// serializers use the PascalCase default — the source-generated
    /// resolver alone does not enforce snake casing.
    /// </summary>
    public sealed record ConflictDetail(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("job_id")] Guid? JobId);
}
