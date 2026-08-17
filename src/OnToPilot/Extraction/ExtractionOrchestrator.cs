using System.Text;
using Microsoft.Extensions.AI;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Storage;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Extraction;

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
    private readonly TerminologyService _terminology;
    private readonly PromptSnapshotService _promptSnapshot;
    private readonly IExtractionMerger _merger;
    private readonly StoreWrapper _store;
    private readonly TimeProvider _clock;

    public ExtractionOrchestrator(
        ExtractionJobStore jobs,
        IBlobStore blobs,
        IDocumentParser parser,
        Chunker chunker,
        IChatClientFactory chatFactory,
        EndpointCapacityCoordinator capacity,
        TBoxExtractionService tbox,
        ABoxExtractionService abox,
        TerminologyService terminology,
        PromptSnapshotService promptSnapshot,
        IExtractionMerger merger,
        StoreWrapper store,
        TimeProvider clock)
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
        // `http://ontopilot.local/ks/{publicId}`). Falling back to a
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
        var job = await _jobs.CreateAsync(
            request.KnowledgeSystemId, kind, request.Model, chunkIds, totalChunks, cancellationToken)
            .ConfigureAwait(false);

        // Background work runs on a thread-pool worker with its own
        // ExecutionContext so the orchestrator's caller (an HTTP request)
        // does not flow AsyncLocal state into the extraction. The chat
        // capacity coordinator relies on AsyncLocal to distinguish
        // re-entrant acquires within one job from acquires by another
        // worker — sharing state across requests would let two independent
        // extractions oversubscribe the same endpoint.
        var ksContext = new KsContext(GraphIri: ksEntity.GraphIri, BaseIri: ksEntity.BaseIri);
        // The chat client is created eagerly so the chat capacity coordinator
        // sees a stable AsyncLocal scope for the whole extraction run, and so
        // disposal is guaranteed even if the runner never gets a chance to
        // observe a failure (e.g. parser exception before phase 1).
        var chat = _chatFactory.Create(request.ToProviderConfig());
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
    /// Terminology sync runs after the TBox layer so the job row reports
    /// <c>terms_added</c> / <c>terms_mapped</c> even on single-layer runs.
    /// </summary>
    private async Task<bool> TBoxOnlyRunnerAsync(JobRunContext ctx)
    {
        var promptSnapshot = _promptSnapshot.SnapshotAsync(
            new Dictionary<string, string> { [TBoxExtractionService.PromptKey] = TBoxExtractionService.SystemPrompt });
        var succeeded = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.TBoxGraph,
            phase: ExtractionPhase.TBox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                await _tbox.ExtractAsync(
                    ctx.Chat, ctx.KsContext, chunk, ct).ConfigureAwait(false),
            merger: delta => _merger.MergeTBox(ctx.KsContext, (TBoxDelta)delta),
            recordMergeAsync: (id, result, ct) => _jobs.RecordTBoxMergeAsync(id, result, ct),
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!succeeded) return false;

        await RunTerminologyAsync(ctx, totalProcessed: ctx.Chunks.Count).ConfigureAwait(false);
        await _jobs.SetPromptSnapshotAsync(ctx.JobId, promptSnapshot, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>ABox-only runner. Same contract as <see cref="TBoxOnlyRunnerAsync"/>.</summary>
    private async Task<bool> ABoxOnlyRunnerAsync(JobRunContext ctx)
    {
        var labels = ExistingClassLabels(ctx.KsContext);
        var promptSnapshot = _promptSnapshot.SnapshotAsync(
            new Dictionary<string, string> { [ABoxExtractionService.PromptKey] = ABoxExtractionService.SystemPrompt });
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
            new Dictionary<string, string>
            {
                [TBoxExtractionService.PromptKey] = TBoxExtractionService.SystemPrompt,
                [ABoxExtractionService.PromptKey] = ABoxExtractionService.SystemPrompt,
            });

        var tboxOk = await RunLayerAsync(
            ctx,
            graphIri: ctx.KsContext.TBoxGraph,
            phase: ExtractionPhase.TBox,
            baseProcessedOffset: 0,
            extractor: async (chunk, ct) =>
                await _tbox.ExtractAsync(
                    ctx.Chat, ctx.KsContext, chunk, ct).ConfigureAwait(false),
            merger: delta => _merger.MergeTBox(ctx.KsContext, (TBoxDelta)delta),
            recordMergeAsync: (id, result, ct) => _jobs.RecordTBoxMergeAsync(id, result, ct),
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!tboxOk) return false;

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
            await _jobs.RecordTerminologyAsync(ctx.JobId, term, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            termCapture.MarkError();
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
    /// </summary>
    private async Task<bool> RunLayerAsync(
        JobRunContext ctx,
        string graphIri,
        ExtractionPhase phase,
        int baseProcessedOffset,
        Func<ChunkSpan, CancellationToken, Task<object>> extractor,
        Func<object, ExtractionMergeResult> merger,
        Func<Guid, ExtractionMergeResult, CancellationToken, Task> recordMergeAsync,
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
                await using (var lease = await _capacity.AcquireAsync(
                    new EndpointCapacityKey("chat", ctx.KsContext.GraphIri),
                    permits: 1,
                    cancellationToken).ConfigureAwait(false))
                {
                    var delta = await extractor(chunk, cancellationToken).ConfigureAwait(false);
                    merged = merger(delta);
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

    /// <summary>Read + parse + chunk the uploaded document once at boot.</summary>
    private async Task<(IReadOnlyList<ChunkSpan> Chunks, ParseResult Result)> ReadDocumentAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
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