using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Parsing;

namespace ISEStudio.Integration;

/// <summary>
/// Implementation of <see cref="IExtractionApplicationService"/>.
/// Unpacks each <see cref="InternalRequest"/> (path / query / body /
/// actor), delegates to the extraction pipeline
/// (<see cref="ExtractionOrchestrator"/> for the three <c>run*</c>
/// arms and <see cref="ExtractionJobStore"/> for the two read arms),
/// and returns the wire DTO the dispatcher serialises.
///
/// <para>The extraction guard
/// (<c>RunWithExtractionGuardAsync</c>) and the schema-compatible
/// empty payload fallback envelopes
/// (<c>EmptyExtractionJob()</c> / <c>Array.Empty&lt;object&gt;()</c>)
/// all live on the dispatcher arm layer &mdash; the application
/// service is a thin envelope-unpacking shim, identical in shape to
/// <see cref="OntologyApplicationService"/> / <see cref="VocabularyApplicationService"/>.</para>
///
/// <para>The body deserialiser accepts both the
/// <see cref="ExtractionRequest"/> shape (caller-supplied
/// <c>knowledge_system_id</c>, <c>blob_sha</c>, <c>file_name</c>,
/// <c>provider</c>, <c>model</c>, <c>endpoint</c>) and the
/// frontend-flavoured <c>{chunk_ids, model}</c> shape the
/// <c>/extract-all</c> route sends. The latter resolves the LLM
/// provider from the knowledge system row (or system config
/// fallback) and the chunk spans from the chunk rows so the frontend
/// does not have to send the LLM credentials or pre-resolve
/// <c>ChunkSpan</c> instances.</para>
/// </summary>
public sealed class ExtractionApplicationService : IExtractionApplicationService
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ExtractionJobStore _jobs;
    private readonly ExtractionOrchestrator _orchestrator;
    private readonly IServiceProvider _services;

    public ExtractionApplicationService(
        ExtractionJobStore jobs,
        ExtractionOrchestrator orchestrator,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(services);
        _jobs = jobs;
        _orchestrator = orchestrator;
        _services = services;
    }

    // -----------------------------------------------------------------
    // ext.list_jobs / ext.get_job — read arms
    // -----------------------------------------------------------------

    public async Task<object?> ListJobsAsync(InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var rows = await _jobs.ListAsync(request.KnowledgeSystemGuid.Value, ct)
            .ConfigureAwait(false);
        return rows.Select(ExtractionJobOut.From).ToList();
    }

    public async Task<object?> GetJobAsync(InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        if (!Guid.TryParse(request.ResourceId, out var jobId)) return null;
        var row = await _jobs.GetAsync(jobId, ct).ConfigureAwait(false);
        // Job id is scoped to its KS: a job owned by a different KS
        // surfaces as null so the dispatcher returns EmptyExtractionJob()
        // — matches the Python 404 envelope without forcing the
        // dispatcher to throw.
        if (row is null) return null;
        return ExtractionJobOut.From(row);
    }

    // -----------------------------------------------------------------
    // ext.run* — three arms share one entry point
    // -----------------------------------------------------------------

    public async Task<object?> RunAsync(
        InternalRequest request, string runKind, CancellationToken ct)
    {
        var frontendBody = DeserializeBody<FrontendExtractionRequest>(request);
        var body = frontendBody?.ChunkIds is not null
            ? await BuildFrontendExtractionRequestAsync(request, frontendBody, ct)
                .ConfigureAwait(false)
            : DeserializeBody<ExtractionRequest>(request);
        if (body is null)
        {
            throw new InvalidOperationException(
                "extraction body is required (knowledge_system_id, blob_sha, " +
                "file_name, provider, model, endpoint).");
        }
        if (request.KnowledgeSystemGuid is Guid knowledgeSystemId)
        {
            body = body with { KnowledgeSystemId = knowledgeSystemId };
        }

        var job = runKind switch
        {
            "extraction.run"           => await _orchestrator.StartTBoxAsync(body, ct)
                .ConfigureAwait(false),
            "extraction.run_combined"  => await _orchestrator.StartCombinedAsync(body, ct)
                .ConfigureAwait(false),
            "extraction.run_instances" => await _orchestrator.StartABoxAsync(body, ct)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unknown extraction run kind '{runKind}'."),
        };

        return ExtractionJobOut.From(job);
    }

    // -----------------------------------------------------------------
    // Frontend-flavoured request shape
    // -----------------------------------------------------------------

    /// <summary>
    /// Body shape the <c>/extract-all</c> route sends: a list of
    /// chunk ids plus an optional model override. The service fills in
    /// the rest (provider config, blob metadata, parsed chunks) from
    /// the knowledge system row + system config + chunk rows.
    /// </summary>
    private sealed record FrontendExtractionRequest(List<Guid>? ChunkIds, string? Model);

    private async Task<ExtractionRequest> BuildFrontendExtractionRequestAsync(
        InternalRequest request,
        FrontendExtractionRequest body,
        CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is not Guid knowledgeSystemId)
        {
            throw new InvalidOperationException("Knowledge system id is required.");
        }
        if (body.ChunkIds is not { Count: > 0 })
        {
            throw new InvalidOperationException("No chunks selected.");
        }

        var db = _services.GetRequiredService<ISEStudioDbContext>();
        var knowledgeSystem = await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == knowledgeSystemId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Knowledge system {knowledgeSystemId} not found.");
        var systemConfig = await db.SystemConfigs.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var providerId = knowledgeSystem.LlmProviderId ?? systemConfig?.LlmProviderId
            ?? throw new InvalidOperationException(
                "No LLM provider is configured for this knowledge system.");
        var provider = await db.Providers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"LLM provider {providerId} not found.");

        var requestedIds = body.ChunkIds.Distinct().ToList();
        var chunkRows = await (
                from chunk in db.Chunks.AsNoTracking()
                join document in db.Documents.AsNoTracking() on chunk.DocumentId equals document.Id
                where requestedIds.Contains(chunk.Id)
                    && document.KnowledgeSystemId == knowledgeSystemId
                select chunk)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (chunkRows.Count != requestedIds.Count)
        {
            throw new InvalidOperationException(
                "One or more selected chunks were not found in this knowledge system.");
        }

        var chunksById = chunkRows.ToDictionary(chunk => chunk.Id);
        var selectedChunks = requestedIds.Select(id =>
        {
            var chunk = chunksById[id];
            return new ChunkSpan(
                chunk.Idx,
                chunk.Text,
                chunk.CharStart,
                chunk.CharEnd,
                chunk.TokenEstimate);
        }).ToList();
        var model = !string.IsNullOrWhiteSpace(body.Model)
            ? body.Model
            : knowledgeSystem.LlmModel ?? systemConfig?.ExtractModel ?? provider.Model;

        return new ExtractionRequest(
            knowledgeSystemId,
            "<already-read>",
            string.Empty,
            "openai-compatible",
            model,
            provider.BaseUrl,
            provider.ApiKey,
            provider.ConcurrencyLimit,
            selectedChunks);
    }

    private static T? DeserializeBody<T>(InternalRequest request) where T : class
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null) return null;
        if (raw is T typed) return typed;
        if (raw is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), DeserializeOptions);
        }
        return null;
    }
}