using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Knowledge;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Extraction;

/// <summary>
/// Internal carrier passed from <see cref="ExtractionOrchestrator.StartAsync"/>
/// into the per-kind runner. Captures the parsed chunks once so the runner
/// (which may run as multiple phases for <c>kind="both"</c>) does not have
/// to re-fetch the blob from object storage on each phase.
/// </summary>
internal sealed record JobRunContext(
    Guid JobId,
    ExtractionRequest Request,
    KsContext KsContext,
    IReadOnlyList<ChunkSpan> Chunks,
    IChatClient Chat);

/// <summary>
/// One chunk's verified TBox candidate set: the merged delta plus the full
/// <see cref="TBoxVerifyResult"/> so the per-chunk
/// <see cref="ExtractionMergeResult"/> can carry the
/// <see cref="RejectedClass"/> / <see cref="RecoveredClass"/> lists into the
/// post-extraction corpus recovery pass.
/// </summary>
internal sealed record VerifiedTBox(TBoxDelta Delta, TBoxVerifyResult? Verify);

/// <summary>
/// Per-chunk roll-up handed to the corpus / hierarchy recovery passes. The
/// chunk text is captured alongside the rejections so the recovery prompt
/// can quote the source verbatim; only the rejections are forwarded — the
/// rest of the verdict (adjudicator reasons, denotation stats) lives inside
/// the per-chunk <see cref="ExtractionMergeResult"/> for the audit trail.
/// </summary>
internal sealed record ChunkVerifyOutcome(
    int ChunkId,
    string Text,
    IReadOnlyList<RejectedClass> Rejected);

/// <summary>
/// Top-level extraction job runner.
///
/// <para>The orchestrator owns the job lifecycle:
/// creates the <see cref="Infrastructure.Persistence.Entities.ExtractionJobEntity"/>
/// row, dispatches the actual work onto the thread pool via
/// <see cref="Task.Run"/>, and exposes <c>Start*</c> entry points that
/// return the freshly created job row so callers (controllers, the future
/// upload endpoint) can poll for status.</para>
///
/// <para>Atomicity contract (load-bearing): when any phase's merge throws,
/// the orchestrator's <see cref="StoreWrapper.CaptureAsync(string, bool, TimeSpan?, CancellationToken)"/>
/// block for that phase has <see cref="QuadChangeCapture.MarkError"/>
/// called on it before disposal, so the RDF writes the merger already
/// produced are reverted in the same logical operation as the SQL
/// <c>failed</c> status write. See the
/// <c>Failed_merge_reverts_rdf_and_marks_job_failed</c> test.</para>
/// </summary>
public sealed class ExtractionOrchestrator
{
    private readonly ExtractionJobStore _jobs;
    private readonly IBlobStore _blobs;
    private readonly IDocumentParser _parser;
    private readonly Chunker _chunker;
    private readonly IChatClientFactory _chatFactory;
    private readonly EndpointCapacityCoordinator _capacity;
    private readonly TBoxExtractionService _tbox;
    private readonly ABoxExtractionService _abox;
    private readonly ITerminologySync _terminology;
    private readonly PromptSnapshotService _promptSnapshot;
    private readonly IExtractionMerger _merger;
    private readonly StoreWrapper _store;
    private readonly TimeProvider _clock;
    private readonly ISEStudioOptions _options;

    /// <summary>
    /// Scope factory for the post-TBox agent chain. The orchestrator is a
    /// singleton whose background work outlives any HTTP request, so the
    /// scoped services the chain needs (<see cref="Conflicts.ConflictService"/>,
    /// <see cref="Conflicts.ConflictAgent"/>, <see cref="Ontology.StructureAgent"/>,
    /// <see cref="Knowledge.KnowledgeStatsService"/>) are resolved from a
    /// fresh scope per job — the same pattern
    /// <see cref="ExtractionJobStore"/> uses for the completion-time stats
    /// refresh. Null in hand-built test orchestrators, where the agent
    /// chain is skipped entirely.
    /// </summary>
    private readonly IServiceScopeFactory? _scopes;

    /// <summary>
    /// Per-chunk TBox role verifier (critic → adjudicator → denotation,
    /// Python <c>_verify_tbox_candidates</c>). Stateless like
    /// <see cref="TBoxExtractionService"/>, so it is injected directly. Null
    /// in hand-built test orchestrators, where verification is skipped.
    /// </summary>
    private readonly TBoxVerifyService? _verify;

    /// <summary>
    /// Dovetail-generated chunk-level pipeline (critic → adjudicator →
    /// denotation → merge). Preferred over <see cref="_verify"/> when
    /// registered in DI; the service is the legacy fallback. Null in
    /// hand-built test orchestrators where the pipeline is not registered.
    /// </summary>
    private readonly TBoxChunkPipeline? _chunkPipeline;

    /// <summary>
    /// Job-level corpus recovery pass (Python
    /// <c>_recover_rejected_classes</c>): revisits every per-chunk rejection
    /// with cross-chunk evidence and re-decides them. Like
    /// <see cref="_verify"/>, null in hand-built test orchestrators where
    /// the second-pass is skipped.
    /// </summary>
    private readonly CorpusRecoveryService? _corpus;

    /// <summary>
    /// Per-chunk hierarchy recovery pass (Python <c>_recover_hierarchy_one</c>):
    /// second-pass edge extraction against the merged class vocabulary. Same
    /// optional-seam pattern as <see cref="_verify"/> and <see cref="_corpus"/>.
    /// </summary>
    private readonly HierarchyRecoveryService? _hierarchy;

    public ExtractionOrchestrator(
        ExtractionJobStore jobs,
        IBlobStore blobs,
        IDocumentParser parser,
        Chunker chunker,
        IChatClientFactory chatFactory,
        EndpointCapacityCoordinator capacity,
        TBoxExtractionService tbox,
        ABoxExtractionService abox,
        ITerminologySync terminology,
        PromptSnapshotService promptSnapshot,
        IExtractionMerger merger,
        StoreWrapper store,
        TimeProvider clock,
        IOptions<ISEStudioOptions>? options = null,
        TBoxVerifyService? verify = null,
        CorpusRecoveryService? corpus = null,
        HierarchyRecoveryService? hierarchy = null,
        IServiceScopeFactory? scopes = null,
        TBoxChunkPipeline? chunkPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(chatFactory);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(tbox);
        ArgumentNullException.ThrowIfNull(abox);
        ArgumentNullException.ThrowIfNull(terminology);
        ArgumentNullException.ThrowIfNull(promptSnapshot);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _jobs = jobs;
        _blobs = blobs;
        _parser = parser;
        _chunker = chunker;
        _chatFactory = chatFactory;
        _capacity = capacity;
        _tbox = tbox;
        _abox = abox;
        _terminology = terminology;
        _promptSnapshot = promptSnapshot;
        _merger = merger;
        _store = store;
        _clock = clock;
        _options = options?.Value ?? new ISEStudioOptions();
        _verify = verify;
        _corpus = corpus;
        _hierarchy = hierarchy;
        _scopes = scopes;
        _chunkPipeline = chunkPipeline;
    }

    // ------------------------------------------------------------------
    // Entry points
    // ------------------------------------------------------------------

    /// <summary>Start a TBox-only extraction run.</summary>
    public Task<Infrastructure.Persistence.Entities.ExtractionJobEntity> StartTBoxAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken) =>
        StartAsync(request, ExtractionWire.KindTBox, TBoxOnlyRunnerAsync, cancellationToken);

    /// <summary>Start an ABox-only extraction run.</summary>
    public Task<Infrastructure.Persistence.Entities.ExtractionJobEntity> StartABoxAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken) =>
        StartAsync(request, ExtractionWire.KindABox, ABoxOnlyRunnerAsync, cancellationToken);

    /// <summary>
    /// Start a combined TBox+ABox run. The job row reports a single
    /// <c>total_chunks = 2 * N</c> progress counter so the progress bar
    /// completes when both phases finish.
    /// </summary>
    public Task<Infrastructure.Persistence.Entities.ExtractionJobEntity> StartCombinedAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken) =>
        StartAsync(request, ExtractionWire.KindBoth, CombinedRunnerAsync, cancellationToken);

    // ------------------------------------------------------------------
    // Boot — common to every public entry point
    // ------------------------------------------------------------------

    private async Task<Infrastructure.Persistence.Entities.ExtractionJobEntity> StartAsync(
        ExtractionRequest request,
        string kind,
        Func<JobRunContext, Task<bool>> runner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(runner);

        var (chunks, _) = await ReadDocumentAsync(request, cancellationToken).ConfigureAwait(false);
        var chunkIds = chunks.Select(c => c.Idx).ToList();

        // Resolve the knowledge system row up-front so the graph IRI we
        // write into matches the row the rest of the system has agreed on
        // (the production backend stamps it as
        // `http://goodcrew.local/ks/{publicId}`). Falling back to a
        // derived IRI here would let a stale `GraphIri` column slip past
        // every extraction, so the row is the source of truth.
        var ksEntity = await _jobs.GetKnowledgeSystemAsync(request.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Knowledge system {request.KnowledgeSystemId} not found.");

        // Combined runs walk every chunk twice (once per layer), so the
        // total chunks progress counter must reflect that — otherwise the
        // progress bar never reaches 100% on a successful combined run.
        var totalChunks = kind == ExtractionWire.KindBoth ? chunkIds.Count * 2 : chunkIds.Count;

        // Validate provider configuration before inserting a pending job.
        // Otherwise a synchronous client-construction failure leaves an
        // orphan row that blocks every subsequent extraction as "active".
        var chat = _chatFactory.Create(request.ToProviderConfig());
        Infrastructure.Persistence.Entities.ExtractionJobEntity job;
        try
        {
            job = await _jobs.CreateAsync(
                request.KnowledgeSystemId, kind, request.Model, chunkIds, totalChunks, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            chat.Dispose();
            throw;
        }

        // Background work runs on a thread-pool worker with its own
        // ExecutionContext so the orchestrator's caller (an HTTP request)
        // does not flow AsyncLocal state into the extraction. The chat
        // capacity coordinator relies on AsyncLocal to distinguish
        // re-entrant acquires within one job from acquires by another
        // worker — sharing state across requests would let two independent
        // extractions oversubscribe the same endpoint.
        var ksContext = new KsContext(GraphIri: ksEntity.GraphIri, BaseIri: ksEntity.BaseIri, Name: ksEntity.Name);
        var runContext = new JobRunContext(job.Id, request, ksContext, chunks, chat);

        // SuppressFlow keeps the chat capacity coordinator's AsyncLocal
        // re-entry tracking from leaking in from the caller's flow. Without
        // this the first acquire inside the background task would see the
        // caller's AsyncLocal state, classify itself as a re-entry, and
        // immediately satisfy without consuming a permit — which then
        // blocks a real concurrent caller behind an infinite "reentrant"
        // claim. Every job runs on its own ExecutionContext.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunJobSafelyAsync(runContext, runner).ConfigureAwait(false);
                }
                finally
                {
                    chat.Dispose();
                }
            });
        }

        return job;
    }

    /// <summary>
    /// Wrap the user-supplied runner so any uncaught exception still leaves
    /// the job in a terminal failed state. The runner itself is responsible
    /// for atomic RDF/SQL rollback within its own phase; this catch-all is
    /// the last-line-of-defence safety net for failures before or after the
    /// capture block (e.g. blob read, parser crash).
    /// </summary>
    private async Task RunJobSafelyAsync(
        JobRunContext context,
        Func<JobRunContext, Task<bool>> runner)
    {
        await _jobs.MarkRunningAsync(context.JobId, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var succeeded = await runner(context).ConfigureAwait(false);
            if (!succeeded) return; // runner already marked the job failed.
            await _jobs.MarkCompletedAsync(context.JobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeMarkFailedAsync(context.JobId, "Cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeMarkFailedAsync(context.JobId, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task SafeMarkFailedAsync(Guid jobId, string error)
    {
        try
        {
            await _jobs.MarkFailedAsync(jobId, error, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // If the failure-marker write itself fails the job is left in
            // its previous state; the boot-time StaleJobRecoveryService
            // will pick it up on the next process restart.
        }
    }

    // ------------------------------------------------------------------
    // Phase runners
    // ------------------------------------------------------------------

    /// <summary>
    /// TBox-only runner. Returns <c>true</c> on success, <c>false</c> on
    /// atomic failure (job already marked failed inside the capture's catch).
    /// The agent chain (conflicts → structure) runs right after the TBox
    /// layer, mirroring Python's <c>_run_extraction_job</c>; terminology
    /// sync runs after it so the job row reports <c>terms_added</c> /
    /// <c>terms_mapped</c> even on single-layer runs.
    /// </summary>
    private async Task<bool> TBoxOnlyRunnerAsync(JobRunContext ctx)
    {
        var promptSnapshot = _promptSnapshot.SnapshotAsync(BuildTBoxPromptSnapshot());
        var perChunk = new List<ChunkVerifyOutcome>();
        var succeeded = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.TBoxGraph,
            phase: ExtractionPhase.TBox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                (object)await ExtractAndVerifyAsync(ctx, chunk, ct).ConfigureAwait(false),
            merger: item => _merger.MergeTBox(ctx.KsContext, ((VerifiedTBox)item).Delta, ((VerifiedTBox)item).Verify),
            recordMergeAsync: (id, result, ct) => _jobs.RecordTBoxMergeAsync(id, result, ct),
            onChunk: (i, item) =>
            {
                var verified = (VerifiedTBox)item;
                perChunk.Add(new ChunkVerifyOutcome(
                    ctx.Chunks[i].Idx,
                    ctx.Chunks[i].Text,
                    verified.Verify?.Rejections ?? Array.Empty<RejectedClass>()));
                return default;
            },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!succeeded) return false;

        await RunCorpusRecoveryAsync(ctx, perChunk).ConfigureAwait(false);
        await RunHierarchyRecoveryAsync(ctx, perChunk).ConfigureAwait(false);

        await RunAgentChainAsync(ctx).ConfigureAwait(false);
        await RunTerminologyAsync(ctx, totalProcessed: ctx.Chunks.Count).ConfigureAwait(false);
        await _jobs.SetPromptSnapshotAsync(ctx.JobId, promptSnapshot, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>ABox-only runner. Same contract as <see cref="TBoxOnlyRunnerAsync"/>.</summary>
    private async Task<bool> ABoxOnlyRunnerAsync(JobRunContext ctx)
    {
        var labels = ExistingClassLabels(ctx.KsContext);
        var promptSnapshot = _promptSnapshot.SnapshotAsync(
            new Dictionary<string, string> { [ABoxExtractionService.PromptKey] = _abox.ResolveSystemPrompt() });
        var succeeded = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.ABoxGraph,
            phase: ExtractionPhase.ABox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                (object)await _abox.ExtractAsync(
                    ctx.Chat, ctx.KsContext, chunk, labels, ct).ConfigureAwait(false),
            merger: delta => _merger.MergeABox(ctx.KsContext, (ABoxDelta)delta),
            recordMergeAsync: (id, result, ct) => _jobs.RecordABoxMergeAsync(id, result, ct),
            onChunk: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!succeeded) return false;

        await RunTerminologyAsync(ctx, totalProcessed: ctx.Chunks.Count).ConfigureAwait(false);
        await _jobs.SetPromptSnapshotAsync(ctx.JobId, promptSnapshot, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Combined TBox-then-ABox runner. <c>total_chunks = 2*N</c> for progress
    /// reporting; the TBox phase advances 0..N and the ABox phase advances
    /// N..2N. The terminology sync runs after both layers, on a different
    /// graph (the vocabulary graph), so it gets its own capture.
    /// </summary>
    private async Task<bool> CombinedRunnerAsync(JobRunContext ctx)
    {
        var labels = ExistingClassLabels(ctx.KsContext);
        var promptSnapshot = _promptSnapshot.SnapshotAsync(
            new Dictionary<string, string>(BuildTBoxPromptSnapshot())
            {
                [ABoxExtractionService.PromptKey] = _abox.ResolveSystemPrompt(),
            });

        var perChunk = new List<ChunkVerifyOutcome>();
        var tboxOk = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.TBoxGraph,
            phase: ExtractionPhase.TBox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                (object)await ExtractAndVerifyAsync(ctx, chunk, ct).ConfigureAwait(false),
            merger: item => _merger.MergeTBox(ctx.KsContext, ((VerifiedTBox)item).Delta, ((VerifiedTBox)item).Verify),
            recordMergeAsync: (id, result, ct) => _jobs.RecordTBoxMergeAsync(id, result, ct),
            onChunk: (i, item) =>
            {
                var verified = (VerifiedTBox)item;
                perChunk.Add(new ChunkVerifyOutcome(
                    ctx.Chunks[i].Idx,
                    ctx.Chunks[i].Text,
                    verified.Verify?.Rejections ?? Array.Empty<RejectedClass>()));
                return default;
            },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!tboxOk) return false;

        // Agent chain runs between the layers, exactly where Python's
        // _run_combined_extraction_job places it: predicate merges the
        // conflict agent recommends must act on a still-empty ABox, and the
        // structure agent's attached classes must exist before ABox chunks
        // type against them.
        await RunAgentChainAsync(ctx).ConfigureAwait(false);

        // Corpus + hierarchy recovery run between TBox and ABox, mirroring
        // Python extract.py:1629-1708: the agents may have merged / attached
        // classes that change the vocabulary the recovery prompts see, and
        // the edges / classes it produces must exist before ABox chunks type
        // against them.
        await RunCorpusRecoveryAsync(ctx, perChunk).ConfigureAwait(false);
        await RunHierarchyRecoveryAsync(ctx, perChunk).ConfigureAwait(false);

        // After TBox completes, refresh the label set so ABox chunks see the
        // newly minted classes.
        labels = ExistingClassLabels(ctx.KsContext);

        var aboxOk = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.ABoxGraph,
            phase: ExtractionPhase.ABox,
            baseProcessedOffset: ctx.Chunks.Count,
            extractor: async (chunk, ct) =>
                (object)await _abox.ExtractAsync(
                    ctx.Chat, ctx.KsContext, chunk, labels, ct).ConfigureAwait(false),
            merger: delta => _merger.MergeABox(ctx.KsContext, (ABoxDelta)delta),
            recordMergeAsync: (id, result, ct) => _jobs.RecordABoxMergeAsync(id, result, ct),
            onChunk: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!aboxOk) return false;

        await RunTerminologyAsync(ctx, totalProcessed: ctx.Chunks.Count * 2).ConfigureAwait(false);
        await _jobs.SetPromptSnapshotAsync(ctx.JobId, promptSnapshot, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Advisory SKOS terminology sync. Runs on the vocabulary graph (its own
    /// lock target, so it gets a separate capture from the layer's RDF
    /// writes). Failures here are swallowed so a terminology blip can never
    /// fail an otherwise-successful extraction.
    ///
    /// <para>Mirrors <c>backend/app/api/extraction.py:_run_terminology_sync</c>
    /// at the shape the orchestrator needs: the deterministic
    /// <see cref="ITerminologySync.SyncAsync"/> pass runs first, then the
    /// scoped <see cref="TerminologyAgent"/> is invoked against the same
    /// scheme to queue <c>TermProposal</c> rows. Both stages are advisory —
    /// any thrown exception is captured by the outer
    /// <see cref="QuadChangeCapture.MarkError"/> and the job row's
    /// <c>terminology_proposals</c> stays at zero, matching the Python
    /// backend's "best-effort" semantics.</para>
    /// </summary>
    private async Task RunTerminologyAsync(JobRunContext ctx, int totalProcessed)
    {
        await using var termCapture = await _store.CaptureAsync(
            ctx.KsContext.VocabularyGraph, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        try
        {
            var term = _terminology.SyncAsync(ctx.KsContext, CancellationToken.None);
            await _jobs.UpdateProgressAsync(ctx.JobId,
                processedChunks: totalProcessed,
                phase: ExtractionPhase.Terminology.ToWire(),
                appendPhaseToLog: ExtractionPhase.Terminology.ToWire(),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // P3-1 (terminology proposals): deterministic sync was advisory
            // and never queued any. Now that the deterministic pass has
            // stamped the concept scheme, ask the scoped LLM-driven
            // TerminologyAgent to suggest pending TermProposal rows. The
            // agent is Scoped (own DbContext), so we resolve it from a
            // fresh scope the same way the post-TBox agent chain
            // (RunAgentChainAsync) does.
            //
            // Skipped when:
            //   * the operator opted out via ISEStudioOptions
            //     (terminology_suggest_during_extraction)
            //   * no scope factory is wired (hand-built test orchestrators)
            //   * the deterministic sync short-circuited (no SchemeIri) or
            //     errored (term.Error is set)
            if (_options.TerminologySuggestDuringExtraction
                && _scopes is not null
                && term.Error is null
                && !string.IsNullOrEmpty(term.SchemeIri))
            {
                term = await RunTerminologyAgentAsync(ctx, term).ConfigureAwait(false);
            }

            await _jobs.RecordTerminologyAsync(ctx.JobId, term, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            termCapture.MarkError();
        }
    }

    /// <summary>
    /// Resolve the scoped <see cref="TerminologyAgent"/> and ask it for
    /// pending <c>TermProposal</c> rows scoped to the scheme the
    /// deterministic sync just anchored. Returns the original
    /// <see cref="TerminologyResult"/> with <c>ProposalsQueued</c>
    /// replaced by the agent's accepted-row count. Any exception (missing
    /// LLM provider, transient HTTP failure, etc.) propagates — the outer
    /// <see cref="RunTerminologyAsync"/> catch turns it into a
    /// <see cref="QuadChangeCapture.MarkError"/> rather than failing the
    /// job.
    /// </summary>
    private async Task<TerminologyResult> RunTerminologyAgentAsync(
        JobRunContext ctx,
        TerminologyResult term)
    {
        using var scope = _scopes!.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ISEStudioDbContext>();

        var ks = await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ctx.Request.KnowledgeSystemId, CancellationToken.None)
            .ConfigureAwait(false);
        if (ks is null)
        {
            return term;
        }

        // job.ChunkIds stores ChunkSpan.Idx (an in-memory 0-based index,
        // not ChunkEntity.Id), so we cannot feed it to the agent
        // directly. Query the parsed-document chunks belonging to this
        // knowledge system, ordered for stable propose prompts (Python
        // _terminology_rows orders by document then chunk order too).
        // ChunkEntity has no `Document` navigation property — the join is
        // explicit, mirroring TerminologyAgent.LoadChunksAsync. Phase 3:
        // legacy_id 列已退役; we hand the agent Guid PKs.
        var chunkIds = await db.Chunks.AsNoTracking()
            .Join(db.Documents,
                c => c.DocumentId,
                d => d.Id,
                (c, d) => new { Chunk = c, Document = d })
            .Where(join => join.Document.KnowledgeSystemId == ks.Id
                && join.Document.ParseStatus == "parsed")
            .OrderBy(join => join.Chunk.DocumentId).ThenBy(join => join.Chunk.Idx)
            .Take(_options.TerminologySuggestionMaxChunks)
            .Select(join => join.Chunk.Id)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (chunkIds.Count == 0)
        {
            return term;
        }

        var agent = services.GetRequiredService<TerminologyAgent>();
        var proposals = await agent.SuggestAsync(
            ks, term.SchemeIri!, chunkIds, ctx.Request.Model, CancellationToken.None)
            .ConfigureAwait(false);
        return term with { ProposalsQueued = proposals.Count };
    }

    /// <summary>
    /// Post-TBox agent chain, mirroring Python extraction.py's conflicts →
    /// structure segment (<c>_sync_conflicts_bg</c> →
    /// <c>conflict_agent.resolve_open_conflicts_bg</c> →
    /// <c>structure_agent.attach_isolated_bg</c>, with
    /// <c>job.phase = "conflicts" / "structure"</c> updates and a
    /// <c>refresh_ks_stats</c> at the end). The TBox capture is already
    /// committed when this runs, so each agent opens its own capture for
    /// its own writes.
    ///
    /// <para>The chain runs only when a scope factory is wired (production
    /// DI); hand-built test orchestrators pass null and skip it — the same
    /// optional-seam pattern <see cref="ExtractionJobStore"/> uses for its
    /// completion-time stats refresh. A thrown exception here propagates to
    /// <see cref="RunJobSafelyAsync"/> and fails the job while keeping the
    /// committed TBox layer — exactly like Python, where the agents run
    /// after <c>cap.diff()</c> already released the capture.</para>
    /// </summary>
    private async Task RunAgentChainAsync(JobRunContext ctx)
    {
        if (_scopes is null)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;

        // The chain's services are scoped and share one DbContext instance
        // within this scope, so the conflicts DetectAsync just wrote are
        // visible to the agent's triage query right after.
        await _jobs.UpdateProgressAsync(ctx.JobId,
            processedChunks: ctx.Chunks.Count,
            phase: ExtractionPhase.Conflicts.ToWire(),
            appendPhaseToLog: ExtractionPhase.Conflicts.ToWire(),
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var conflictService = services.GetRequiredService<ConflictService>();
        await conflictService.DetectAsync(ctx.Request.KnowledgeSystemId, CancellationToken.None)
            .ConfigureAwait(false);

        // Python resolve_open_conflicts_bg carries no extraction_active
        // gate (the guard lives in the detect endpoint only), so the
        // pipeline call must skip the agent's own job-active check — the
        // job's running row would otherwise no-op the pass.
        var conflictAgent = services.GetRequiredService<ConflictAgent>();
        await conflictAgent.TriageAsync(
            ctx.Request.KnowledgeSystemId,
            CancellationToken.None,
            model: ctx.Request.Model,
            skipActiveExtractionGate: true).ConfigureAwait(false);

        await _jobs.UpdateProgressAsync(ctx.JobId,
            processedChunks: ctx.Chunks.Count,
            phase: ExtractionPhase.Structure.ToWire(),
            appendPhaseToLog: ExtractionPhase.Structure.ToWire(),
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var structureAgent = services.GetRequiredService<StructureAgent>();
        await structureAgent.AttachIsolatedAsync(
            ctx.Request.KnowledgeSystemId,
            ctx.Request.Model,
            CancellationToken.None,
            skipActiveExtractionGate: true).ConfigureAwait(false);

        // Re-sync cached class/property/axiom counts after the agents may
        // have merged / added classes (Python refresh_ks_stats at
        // extraction.py:344/558). Best-effort like the completion-time
        // refresh in ExtractionJobStore.MarkCompletedAsync: a stats
        // failure must not fail an otherwise-successful extraction.
        try
        {
            var stats = services.GetRequiredService<KnowledgeStatsService>();
            await stats.RefreshAsync(ctx.Request.KnowledgeSystemId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Swallowed — MarkCompletedAsync refreshes again at completion.
        }
    }

    // ------------------------------------------------------------------
    // Per-layer chunk loop
    // ------------------------------------------------------------------

    /// <summary>
    /// Generic chunk loop shared by the three runners. Opens one
    /// <see cref="QuadChangeCapture"/> on <paramref name="graphIri"/> for the
    /// whole phase so a thrown merger rolls every chunk's writes back in a
    /// single atomic revert. The 60s capture timeout guards against a dead
    /// lock on the same graph from a parallel write (e.g. an upload in
    /// flight); production graph-leases normally resolve in milliseconds.
    /// <paramref name="onChunk"/> runs after the lease releases and before
    /// the per-chunk merge row is written, so the runner can observe the
    /// verify result alongside the merger — used by the TBox phase to feed
    /// the corpus / hierarchy recovery passes.
    /// </summary>
    private async Task<bool> RunLayerAsync(
        JobRunContext ctx,
        string graphIri,
        ExtractionPhase phase,
        int baseProcessedOffset,
        Func<ChunkSpan, CancellationToken, Task<object>> extractor,
        Func<object, ExtractionMergeResult> merger,
        Func<Guid, ExtractionMergeResult, CancellationToken, Task> recordMergeAsync,
        Func<int, object, ValueTask>? onChunk,
        CancellationToken cancellationToken)
    {
        var jobId = ctx.JobId;
        var chunks = ctx.Chunks;

        await _jobs.UpdateProgressAsync(jobId,
            processedChunks: baseProcessedOffset,
            phase: phase.ToWire(),
            appendPhaseToLog: phase.ToWire(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await using var capture = await _store.CaptureAsync(
            graphIri, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                ExtractionMergeResult merged;
                // Bucket by the provider endpoint (Constraint 4): two jobs
                // pointed at the same endpoint share a permit budget so
                // the chat provider is never oversubscribed, regardless of
                // which knowledge system they write into. Two jobs pointed
                // at different endpoints flow through independent buckets.
                await using (var lease = await _capacity.AcquireAsync(
                    ctx.Request.CapacityKey,
                    permits: 1,
                    cancellationToken).ConfigureAwait(false))
                {
                    var delta = await extractor(chunk, cancellationToken).ConfigureAwait(false);
                    merged = merger(delta);
                    if (onChunk is not null) await onChunk(i, delta).ConfigureAwait(false);
                }

                await recordMergeAsync(jobId, merged, cancellationToken).ConfigureAwait(false);
                await _jobs.UpdateProgressAsync(jobId,
                    processedChunks: baseProcessedOffset + i + 1,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            capture.MarkError();
            await SafeMarkFailedAsync(jobId, "Cancelled.").ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            capture.MarkError();
            await SafeMarkFailedAsync(jobId, ex.Message).ConfigureAwait(false);
            return false;
        }

        // Commit phase's RDF writes — only reached when every chunk
        // merged without throwing. The capture disposes without
        // MarkError so the graph snapshot is not restored.
        return true;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Extract + verify one chunk's TBox candidates — Python's worker
    /// sequence <c>_extract_for_chunk → _verify_tbox_candidates</c>
    /// (extract.py:1596-1597). Verification runs inside the same capacity
    /// lease as extraction so the extra critic calls respect the provider
    /// budget; a delta with no class / subclass candidates is returned
    /// untouched by <see cref="TBoxVerifyService.VerifyAsync"/>'s early
    /// return without any extra LLM call. Skipped entirely when no verifier
    /// is wired (hand-built test orchestrators). The full
    /// <see cref="TBoxVerifyResult"/> is forwarded to the merger so
    /// <see cref="RejectedClass"/> / <see cref="RecoveredClass"/> lists flow
    /// into the per-chunk result and into the corpus recovery pass.
    ///
    /// <para>When the Dovetail chunk pipeline is registered in DI it is
    /// preferred over the direct service call so the four-stage DAG
    /// (critic → adjudicator [self fail-soft] → denotation → merge) runs
    /// instead of <see cref="TBoxVerifyService.VerifyAsync"/>. The legacy
    /// service is kept as the fallback for hand-built test orchestrators
    /// that bypass DI registration.</para>
    /// </summary>
    private async Task<VerifiedTBox> ExtractAndVerifyAsync(
        JobRunContext ctx, ChunkSpan chunk, CancellationToken cancellationToken)
    {
        var delta = await _tbox.ExtractAsync(ctx.Chat, ctx.KsContext, chunk, cancellationToken)
            .ConfigureAwait(false);
        if (_verify is null)
        {
            return new VerifiedTBox(delta, null);
        }
        // Dovetail pipeline preferred when registered; fall back to direct
        // service call when pipeline is absent (legacy hand-built test
        // orchestrators). Both paths return TBoxVerifyResult.
        var verified = _chunkPipeline is not null
            ? await _chunkPipeline.ExecuteAsync(
                new TBoxChunkInput(chunk.Idx, chunk.Text, delta, ctx.Chat),
                cancellationToken).ConfigureAwait(false)
            : await _verify.VerifyAsync(ctx.Chat, chunk.Text, delta, cancellationToken)
                .ConfigureAwait(false);
        return new VerifiedTBox(verified.Delta, verified);
    }

    /// <summary>
    /// Prompt snapshot entries for a TBox phase: the extractor prompt plus,
    /// when the verifier is wired, the three critic prompts the pipeline
    /// consumes plus, when the corpus / hierarchy recovery passes are
    /// wired, the two selector / recovery / two hierarchy critic / recovery
    /// prompts (Python records every prompt a job actually used).
    /// </summary>
    private Dictionary<string, string> BuildTBoxPromptSnapshot()
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

    /// <summary>
    /// Python <c>_recover_rejected_classes</c>: job-level second pass over
    /// the per-chunk rejection list. Skipped entirely when the service is
    /// not wired (hand-built test orchestrators); failures inside the pass
    /// are swallowed so a recovery blip cannot fail an otherwise-successful
    /// extraction (Python <c>logger.warning</c> only — extract.py:1349-1354,
    /// :1612-1616).
    /// </summary>
    private async Task RunCorpusRecoveryAsync(JobRunContext ctx, IReadOnlyList<ChunkVerifyOutcome> perChunk)
    {
        if (_corpus is null || _verify is null) return;
        if (perChunk.Count == 0) return;

        try
        {
            await _jobs.UpdateProgressAsync(ctx.JobId,
                processedChunks: ctx.Chunks.Count,
                phase: ExtractionPhase.TBox.ToWire(),
                appendPhaseToLog: "corpus-recovery",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var existingNorms = SchemaBuilder.BuildView(ctx.KsContext.TBoxGraph, _store).Classes
                .Select(c => TBoxVerifyService.LabelNorm(c.Label))
                .ToHashSet(StringComparer.Ordinal);

            var recoveryChunks = perChunk
                .Select(p => new CorpusRecoveryChunk(p.ChunkId, p.Text, p.Rejected))
                .ToList();

            var recovered = await _corpus.RecoverAsync(
                ctx.Chat, recoveryChunks, existingNorms, CancellationToken.None).ConfigureAwait(false);

            if (recovered.Classes.Count > 0)
            {
                await MergeCorpusRecoveredAsync(ctx, recovered).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-soft: Python's recovery never fails the job.
        }
    }

    private async Task MergeCorpusRecoveredAsync(JobRunContext ctx, CorpusRecoveryResult recovered)
    {
        await using var capture = await _store.CaptureAsync(
            ctx.KsContext.TBoxGraph, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        try
        {
            foreach (var row in recovered.Classes)
            {
                var delta = new TBoxDelta(
                    new[] { new ClassMutation(row.Label, Comment: null, RoleVerified: true) },
                    Array.Empty<PropertyMutation>(),
                    Array.Empty<PropertyMutation>(),
                    Array.Empty<AxiomMutation>());
                var result = _merger.MergeTBox(ctx.KsContext, delta, verify: null);
                await _jobs.RecordTBoxMergeAsync(ctx.JobId, result, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            capture.MarkError();
            throw;
        }
    }

    /// <summary>
    /// Python <c>_recover_hierarchy_one</c>: per-chunk second pass that
    /// proposes super-classes / edges, then runs both through the
    /// independent critics. Like the corpus pass, skipped when not wired
    /// and failures are swallowed.
    /// </summary>
    private async Task RunHierarchyRecoveryAsync(JobRunContext ctx, IReadOnlyList<ChunkVerifyOutcome> perChunk)
    {
        if (_hierarchy is null || _verify is null) return;
        if (perChunk.Count == 0) return;

        var labels = ExistingClassLabels(ctx.KsContext);

        try
        {
            await _jobs.UpdateProgressAsync(ctx.JobId,
                processedChunks: ctx.Chunks.Count,
                phase: ExtractionPhase.TBox.ToWire(),
                appendPhaseToLog: "hierarchy-recovery",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            foreach (var outcome in perChunk)
            {
                try
                {
                    await using var lease = await _capacity.AcquireAsync(
                        ctx.Request.CapacityKey,
                        permits: 1,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    var grounded = labels
                        .Where(l => RoleEvidence.SurfaceIsGrounded(outcome.Text, l))
                        .Take(400)
                        .ToList();
                    if (grounded.Count == 0) continue;
                    var recovered = await _hierarchy.RecoverAsync(
                        ctx.Chat, outcome.Text, grounded, CancellationToken.None).ConfigureAwait(false);
                    await MergeHierarchyRecoveredAsync(ctx, recovered).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail-soft per chunk — one bad recovery must not lose
                    // the rest, mirroring Python extract.py:1699.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-soft at the outer level.
        }
    }

    private async Task MergeHierarchyRecoveredAsync(JobRunContext ctx, HierarchyRecoveryResult recovered)
    {
        if (recovered.Classes.Count == 0 && recovered.Edges.Count == 0) return;
        await using var capture = await _store.CaptureAsync(
            ctx.KsContext.TBoxGraph, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        try
        {
            var delta = new TBoxDelta(
                recovered.Classes.Select(c => c with { RoleVerified = true }).ToList(),
                Array.Empty<PropertyMutation>(),
                Array.Empty<PropertyMutation>(),
                recovered.Edges.Select(e => new AxiomMutation(
                    Type: "subclass", Sub: e.Sub, Super: e.Super)).ToList());
            var result = _merger.MergeTBox(ctx.KsContext, delta, verify: null);
            await _jobs.RecordTBoxMergeAsync(ctx.JobId, result, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            capture.MarkError();
            throw;
        }
    }

    /// <summary>Read + parse + chunk the uploaded document once at boot.</summary>
    private async Task<(IReadOnlyList<ChunkSpan> Chunks, ParseResult Result)> ReadDocumentAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SelectedChunks is not null)
        {
            return (request.SelectedChunks, new ParseResult(Text: string.Empty, Backend: "selected-chunks"));
        }
        if (!string.IsNullOrEmpty(request.BlobSha) && request.BlobSha != "<already-read>")
        {
            var stream = await _blobs.GetAsync(request.BlobSha, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Blob {request.BlobSha} not found.");
            using (stream)
            {
                var parsed = _parser.Parse(stream, request.FileName);
                return (_chunker.ChunkDocument(parsed), parsed);
            }
        }
        // Subsequent calls in the same job skip the blob read; the chunks
        // are re-derived from a synthetic single-chunk ParseResult so the
        // helpers can share a common shape.
        var fallback = new ParseResult(Text: string.Empty, Backend: "noop");
        return (_chunker.ChunkDocument(fallback), fallback);
    }

    private IReadOnlyCollection<string> ExistingClassLabels(KsContext ksContext)
    {
        var view = SchemaBuilder.BuildView(ksContext.TBoxGraph, _store);
        return view.Classes
            .Select(c => c.Label)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }
}