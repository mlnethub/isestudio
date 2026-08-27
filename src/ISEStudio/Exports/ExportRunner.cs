using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;

namespace ISEStudio.Exports;

/// <summary>
/// Background worker that drains an <see cref="ExportJobEntity"/> row.
///
/// <para>Mirrors <see cref="Extraction.ExtractionOrchestrator"/>'s
/// lifecycle contract:
/// <list type="number">
///   <item><see cref="ExportJobStore.MarkRunningAsync"/> flips
///   status <c>pending</c> → <c>running</c>.</item>
///   <item>For each layer in <see cref="ExportLayer.Expand"/>: take a
///   <see cref="StoreWrapper.CaptureAsync"/> lease, dump the graph via
///   <see cref="StoreWrapper.DumpNQuads(string)"/>, write a shard with
///   <see cref="ExportArtifactStore.WriteShard"/>, and update the
///   processed-statements counter.</item>
///   <item>Write the bundle manifest (one descriptor per shard + a
///   manifest entry).</item>
///   <item><see cref="ExportJobStore.RecordFilesAsync"/> + <see cref="ExportJobStore.MarkCompletedAsync"/>
///   land the terminal row state.</item>
/// </list>
/// </para>
///
/// <para>Failures are swallowed via <see cref="SafeMarkFailedAsync"/> so
/// the orchestrator's last-line-of-defence catch cannot itself throw and
/// strand the row in <c>running</c>. The boot-time
/// <see cref="Infrastructure.Startup.StaleJobRecoveryService"/> picks up
/// any row that does crash mid-flight.</para>
/// </summary>
public sealed class ExportRunner
{
    private readonly ExportJobStore _jobs;
    private readonly ExportArtifactStore _artifacts;
    private readonly StoreWrapper? _store;
    private readonly ReleaseArtifactStore? _releaseArtifacts;
    private readonly TimeProvider _clock;

    public ExportRunner(
        ExportJobStore jobs,
        ExportArtifactStore artifacts,
        StoreWrapper? store,
        ReleaseArtifactStore? releaseArtifacts,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(clock);
        _jobs = jobs;
        _artifacts = artifacts;
        _store = store;
        _releaseArtifacts = releaseArtifacts;
        _clock = clock;
    }

    /// <summary>
    /// Drain <paramref name="job"/> from <c>pending</c> to a terminal
    /// state. Callers are responsible for invoking this on the thread
    /// pool (see <see cref="ExportService.CreateAsync"/>) so the HTTP
    /// request scope returns before the layer dumps complete.
    /// </summary>
    public async Task RunAsync(
        ExportJobEntity job,
        KnowledgeSystemEntity ks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ks);

        await _jobs.MarkRunningAsync(job.Id, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var releaseKey = job.ReleaseId is { } relId ? relId.ToString("N") : null;
            var isReleaseBound = releaseKey is not null;

            if (isReleaseBound)
            {
                // Release-bound: read pre-sharded layers from the immutable
                // release snapshot. No capture lease needed — the snapshot
                // is frozen at publish time. Mirrors Python
                // _run_export release branch (releases.py:242-245).
                if (_releaseArtifacts is null)
                    throw new InvalidOperationException("Release artifact store is not available.");
            }
            else if (_store is null)
            {
                // No Oxigraph (contract-test factory with no RDF root).
                // Fail loudly so the operator can see the export couldn't
                // produce shards; the row's error column surfaces the
                // reason in the API response.
                throw new InvalidOperationException("Graph store is not available.");
            }

            var ksc = KsContext.FromEntity(ks);
            _artifacts.PrepareOutputDir(ks.PublicId);

            var layers = ExportLayer.Expand(job.Layer);
            var files = new List<ExportFileEntry>(layers.Count + 1);
            long total = 0;

            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] nQuads;
                if (isReleaseBound && releaseKey is not null)
                {
                    // Read the layer shard from the release's artifact
                    // directory (written at capture time by
                    // ReleaseManager.CaptureAsync).
                    nQuads = _releaseArtifacts!.Read(releaseKey, LayerToRdf(layer));
                }
                else
                {
                    var graphIri = ReleaseManager.GraphIriFor(ksc, LayerToRdf(layer));
                    // Exclusive lease — slightly more conservative than the
                    // Python `store.read_lock` (shared). MVP; brief doesn't
                    // require concurrent-edit semantics.
                    await using var capture = await _store!.CaptureAsync(
                        graphIri, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60))
                        .ConfigureAwait(false);
                    nQuads = _store.DumpNQuads(graphIri);
                }
                var entry = _artifacts.WriteShard(
                    ks.PublicId, layer, shardIndex: 0, nQuads);
                files.Add(entry);
                total += entry.Statements;
                await _jobs.UpdateProgressAsync(job.Id, (int)total, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Bundle manifest summarises the contents + per-shard
            // descriptors so the API can render the file picker without
            // re-reading the shards.
            var manifest = new
            {
                knowledge_system = new { id = ks.PublicId, name = ks.Name },
                release_id = job.ReleaseId,
                release_version = (string?)null,
                layer = job.Layer,
                format = "application/n-quads",
                compression = "none",
                files = files.Select(f => new
                {
                    f.Name,
                    f.Layer,
                    f.Statements,
                    f.Bytes,
                    f.Sha256,
                }),
            };
            var manifestEntry = _artifacts.WriteManifest(
                ks.PublicId, manifest);
            files.Add(manifestEntry);

            await _jobs.RecordFilesAsync(job.Id, files, CancellationToken.None)
                .ConfigureAwait(false);
            await _jobs.MarkCompletedAsync(job.Id, (int)total, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeMarkFailedAsync(job.Id, "Cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeMarkFailedAsync(job.Id, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort write of <see cref="ExportJobStore.MarkFailedAsync"/>;
    /// swallows any further exception so the runner never throws out of
    /// its top-level catch.
    /// </summary>
    private async Task SafeMarkFailedAsync(Guid jobId, string error)
    {
        try
        {
            await _jobs.MarkFailedAsync(jobId, error, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Boot-time StaleJobRecoveryService will pick this up.
        }
    }

    private static RdfLayer LayerToRdf(string layer) => layer switch
    {
        ExportLayer.TBox => RdfLayer.TBox,
        ExportLayer.Vocabulary => RdfLayer.Vocabulary,
        ExportLayer.ABox => RdfLayer.ABox,
        _ => throw new ArgumentOutOfRangeException(
            nameof(layer), layer, "Unknown export layer."),
    };
}