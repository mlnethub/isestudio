using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;

namespace ISEStudio.Extraction;

public sealed class TBoxExtractionJobHandler : DurableLayerExtractionJobHandlerBase
{
    private readonly TBoxExtractionService _tbox;
    private readonly TBoxVerifyService? _verify;
    private readonly CorpusRecoveryService? _corpus;
    private readonly HierarchyRecoveryService? _hierarchy;

    public TBoxExtractionJobHandler(
        ISEStudioDbContext db,
        IChatClientFactory chatFactory,
        ExtractionJobStore jobs,
        JobPipelineRouter router,
        PromptSnapshotService promptSnapshot,
        TBoxExtractionService tbox,
        TBoxVerifyService? verify = null,
        CorpusRecoveryService? corpus = null,
        HierarchyRecoveryService? hierarchy = null)
        : base(db, chatFactory, jobs, router, promptSnapshot)
    {
        _tbox = tbox;
        _verify = verify;
        _corpus = corpus;
        _hierarchy = hierarchy;
    }

    public override string Kind => ExtractionWire.KindTBox;

    protected override JobKind JobKind => JobKind.TBoxOnly;

    protected override IReadOnlyDictionary<string, string> BuildPromptMap()
    {
        var prompts = new Dictionary<string, string>
        {
            [TBoxExtractionService.PromptKey] = _tbox.ResolveSystemPrompt(),
        };

        if (_verify is not null)
        {
            prompts[TBoxVerifyService.BoundaryCriticKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.BoundaryCriticKey);
            prompts[TBoxVerifyService.BoundaryAdjudicatorKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.BoundaryAdjudicatorKey);
            prompts[TBoxVerifyService.DenotationCriticKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.DenotationCriticKey);
        }

        if (_corpus is not null)
        {
            prompts[CorpusRecoveryService.EvidenceSelectorKey] =
                _corpus.ResolveSystemPrompt(CorpusRecoveryService.EvidenceSelectorKey);
            prompts[CorpusRecoveryService.CorpusRecoveryKey] =
                _corpus.ResolveSystemPrompt(CorpusRecoveryService.CorpusRecoveryKey);
        }

        if (_hierarchy is not null)
        {
            prompts[HierarchyRecoveryService.HierarchyCriticKey] =
                _hierarchy.ResolveSystemPrompt(HierarchyRecoveryService.HierarchyCriticKey);
            prompts[HierarchyRecoveryService.HierarchyRecoveryKey] =
                _hierarchy.ResolveSystemPrompt(HierarchyRecoveryService.HierarchyRecoveryKey);
        }

        return prompts;
    }
}

public abstract class DurableLayerExtractionJobHandlerBase : IExtractionJobHandler
{
    private readonly ISEStudioDbContext _db;
    private readonly IChatClientFactory _chatFactory;
    private readonly ExtractionJobStore _jobs;
    private readonly JobPipelineRouter _router;
    private readonly PromptSnapshotService _promptSnapshot;

    protected DurableLayerExtractionJobHandlerBase(
        ISEStudioDbContext db,
        IChatClientFactory chatFactory,
        ExtractionJobStore jobs,
        JobPipelineRouter router,
        PromptSnapshotService promptSnapshot)
    {
        _db = db;
        _chatFactory = chatFactory;
        _jobs = jobs;
        _router = router;
        _promptSnapshot = promptSnapshot;
    }

    public abstract string Kind { get; }

    protected abstract JobKind JobKind { get; }

    protected abstract IReadOnlyDictionary<string, string> BuildPromptMap();

    public async Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = ExtractionJobPayloadReader.ReadPayload(job);
        var knowledgeSystemId = ExtractionJobPayloadReader.ReadRequiredGuid(
            payload,
            "knowledge_system_id",
            "knowledgeSystemId");
        if (knowledgeSystemId != job.KnowledgeSystemId)
        {
            throw new InvalidOperationException(
                $"Extraction job '{job.Id}' payload knowledge system '{knowledgeSystemId}' does not match the claimed row.");
        }

        var payloadJobId = ExtractionJobPayloadReader.ReadRequiredGuid(payload, "job_id", "jobId");
        if (payloadJobId != job.Id)
        {
            throw new InvalidOperationException(
                $"Extraction job '{job.Id}' payload job id '{payloadJobId}' does not match the claimed row.");
        }

        var sourceVersionId = ExtractionJobPayloadReader.ReadRequiredGuid(payload, "source_version", "sourceVersion");
        var replay = await BuildReplayAsync(knowledgeSystemId, sourceVersionId, job, cancellationToken).ConfigureAwait(false);
        var promptSnapshot = _promptSnapshot.SnapshotAsync(BuildPromptMap());
        var chat = _chatFactory.Create(replay.Request.ToProviderConfig());

        try
        {
            var input = new JobInput(
                JobId: job.Id,
                KnowledgeSystemId: replay.Request.KnowledgeSystemId,
                ChunkIds: replay.Chunks.Select(chunk => chunk.Idx).ToArray(),
                Chat: chat,
                Kind: JobKind,
                InitialVocabulary: null,
                CancellationToken: cancellationToken,
                KsContext: replay.KsContext,
                Request: replay.Request,
                Chunks: replay.Chunks,
                PerChunk: Array.Empty<ChunkVerifyOutcome>());

            var result = await _router.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await _jobs.MarkFailedAsync(
                    job.Id,
                    result.Error ?? "Extraction pipeline failed.",
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await _jobs.SetPromptSnapshotAsync(job.Id, promptSnapshot, CancellationToken.None).ConfigureAwait(false);
            await _jobs.MarkCompletedAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _jobs.MarkFailedAsync(
                job.Id,
                $"{exception.GetType().Name}: {exception.Message}",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            chat.Dispose();
        }
    }

    private async Task<DurableExtractionReplay> BuildReplayAsync(
        Guid knowledgeSystemId,
        Guid sourceVersionId,
        ExtractionJobEntity job,
        CancellationToken cancellationToken)
    {
        var knowledgeSystem = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == knowledgeSystemId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Knowledge system '{knowledgeSystemId}' was not found.");

        var version = await _db.DocumentVersions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sourceVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Document version '{sourceVersionId}' was not found.");
        if (version.KnowledgeSystemId != knowledgeSystemId)
        {
            throw new InvalidOperationException(
                $"Document version '{sourceVersionId}' does not belong to knowledge system '{knowledgeSystemId}'.");
        }

        var chunks = await _db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == sourceVersionId)
            .OrderBy(item => item.Idx)
            .Select(item => new ChunkSpan(
                item.Idx,
                item.Text,
                item.CharStart,
                item.CharEnd,
                item.TokenEstimate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException(
                $"Document version '{sourceVersionId}' does not contain any chunks to replay.");
        }

        var expectedChunkIds = job.ChunkIds ?? new List<int>();
        if (!expectedChunkIds.SequenceEqual(chunks.Select(chunk => chunk.Idx)))
        {
            throw new InvalidOperationException(
                $"Document version '{sourceVersionId}' chunks do not match extraction job '{job.Id}'.");
        }

        var systemConfig = await _db.SystemConfigs.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var providerId = knowledgeSystem.LlmProviderId ?? systemConfig?.LlmProviderId
            ?? throw new InvalidOperationException(
                $"Knowledge system '{knowledgeSystemId}' does not have an LLM provider configured.");
        var provider = await _db.Providers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == providerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"LLM provider '{providerId}' was not found.");

        var model = !string.IsNullOrWhiteSpace(job.Model)
            ? job.Model
            : knowledgeSystem.LlmModel ?? systemConfig?.ExtractModel ?? provider.Model;
        var request = new ExtractionRequest(
            KnowledgeSystemId: knowledgeSystemId,
            BlobSha: "<already-read>",
            FileName: string.Empty,
            Provider: "openai-compatible",
            Model: model,
            Endpoint: provider.BaseUrl,
            ApiKey: provider.ApiKey,
            ConcurrencyLimit: provider.ConcurrencyLimit,
            SelectedChunks: chunks);

        return new DurableExtractionReplay(request, KsContext.FromEntity(knowledgeSystem), chunks);
    }

    private sealed record DurableExtractionReplay(
        ExtractionRequest Request,
        KsContext KsContext,
        IReadOnlyList<ChunkSpan> Chunks);
}