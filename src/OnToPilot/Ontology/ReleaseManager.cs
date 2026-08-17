using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;
using OnToPilot.Application.Foundation;

namespace OnToPilot.Ontology;

/// <summary>
/// Orchestrates immutable release lifecycle: <c>capture</c> freezes the three
/// workspace layers into the artifact store; <c>publish</c> opens a
/// physically separate read-only RocksDB for serving; <c>read published</c>
/// queries that serving store; <c>delete</c> tears down both. The serving
/// store is keyed by release <see cref="Release.Id"/> and lives under
/// <c>{servingRoot}/{id}/</c> so workspace writes after publication never
/// leak into the published view.
/// </summary>
public sealed class ReleaseManager : IDisposable
{
    private readonly StoreWrapper _workspace;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly string _servingRoot;
    private readonly Dictionary<string, PublishedEntry> _published = new(StringComparer.Ordinal);
    // _lock guards the in-memory _published registry (read on publish /
    // delete).
    private readonly object _lock = new();
    // _versionLock serializes the entire CaptureAsync body so two concurrent
    // captures for the same knowledge system cannot both observe an empty
    // artifact store and allocate the same version. Capture is an
    // infrequent operation, so the simpler "one capture at a time" model
    // is preferable to a more elaborate reservation scheme.
    private readonly SemaphoreSlim _versionLock = new(1, 1);
    private bool _disposed;

    private sealed record PublishedEntry(KsContext Ks, StoreWrapper Store);

    public ReleaseManager(
        StoreWrapper workspace,
        ReleaseArtifactStore artifacts,
        string servingRoot)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrEmpty(servingRoot);

        _workspace = workspace;
        _artifacts = artifacts;
        _servingRoot = Path.GetFullPath(servingRoot);
        Directory.CreateDirectory(_servingRoot);
    }

    /// <summary>Path where a release's serving store is rooted (created at publish time).</summary>
    public string ServingPath(string releaseId) =>
        Path.Combine(_servingRoot, releaseId);

    /// <summary>The artifact store backing this manager.</summary>
    public ReleaseArtifactStore Artifacts => _artifacts;

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    /// <summary>
    /// Freeze the three workspace layers into the artifact store. Returns
    /// the draft release (status: capture-only, not yet published). All
    /// three layers are snapshotted inside their own <c>CaptureAsync</c>
    /// windows so a failure in one layer reverts just that layer.
    /// </summary>
    public async Task<Release> CaptureAsync(
        KsContext ks,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Serialize the entire capture against concurrent captures for the
        // same KS. Version allocation + shard write + manifest write must
        // be atomic w.r.t. AllocateVersion() callers; otherwise two
        // concurrent captures can both observe an empty artifact store and
        // both allocate "v1". The version reservation (write the manifest
        // skeleton first, then update with shards) would also work; the
        // whole-body semaphore is simpler and capture is infrequent.
        await _versionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = AllocateVersion();
            var id = Guid.NewGuid().ToString("N");

            var files = new List<ReleaseFileManifest>(3);
            long provenanceCount = 0;

            foreach (var layer in new[] { RdfLayer.TBox, RdfLayer.ABox, RdfLayer.Vocabulary })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var graphIri = GraphIriFor(ks, layer);
                var graph = new OntoNamedNode(graphIri);

                await using var capture = await _workspace.CaptureAsync(
                    graphIri, revertOnError: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var nQuads = _workspace.DumpNQuads(graph);
                _artifacts.Write(id, layer, nQuads);
                files.Add(_artifacts.BuildFileManifest(id, layer, nQuads));
                provenanceCount += ReleaseArtifactStore.StatementCount(nQuads);
            }

            var manifest = new ReleaseManifest(version, files, provenanceCount);
            _artifacts.SaveManifest(id, manifest);
            WriteKsHeader(_artifacts.ReleasePath(id), ks);

            return new Release(id, version, ks, _artifacts.ReleasePath(id));
        }
        finally
        {
            _versionLock.Release();
        }
    }

    // ------------------------------------------------------------------
    // Publish
    // ------------------------------------------------------------------

    /// <summary>
    /// Publish a previously captured release: copy its shards into a fresh
    /// RocksDB at <see cref="ServingPath"/> and open it read-only via
    /// <see cref="StoreWrapper.OpenReadOnly"/>. Workspace writes after
    /// publication are physically isolated — the serving store does not
    /// share storage with the workspace.
    /// </summary>
    public async Task<Release> PublishAsync(
        string releaseId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_artifacts.Exists(releaseId))
            throw new InvalidOperationException($"Release '{releaseId}' does not exist.");

        // Idempotency: if we've already published, return the existing record.
        if (TryGetPublishedEntry(releaseId, out var existing))
        {
            return new Release(releaseId, _artifacts.LoadManifest(releaseId).Version,
                existing.Ks, ServingPath(releaseId));
        }

        var manifest = _artifacts.LoadManifest(releaseId);
        var servingPath = ServingPath(releaseId);

        // Wipe any prior serving directory so the load is deterministic.
        if (Directory.Exists(servingPath))
        {
            Directory.Delete(servingPath, recursive: true);
        }
        Directory.CreateDirectory(servingPath);

        // Capture the Ks from the artifact path encoding. Production code
        // (Stage 3) replaces this with a full EF lookup. We use a header
        // file inside the artifact directory to round-trip the KS IRIs so
        // a cross-process restart can re-open the serving store without
        // re-capturing.
        var ksContext = ReadKsHeader(_artifacts.ReleasePath(releaseId)) ??
            throw new InvalidOperationException(
                $"Release '{releaseId}' has no KsContext header; cannot publish.");

        // 1) Materialize the shards into a fresh writable RocksDB.
        StoreWrapper? writable = null;
        try
        {
            writable = new StoreWrapper(servingPath);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(Path.Combine(_artifacts.ReleasePath(releaseId), file.FileName));
                writable.LoadNQuads(bytes, null);
            }
        }
        finally
        {
            writable?.Dispose();
        }

        // 2) Open the same directory read-only and register it.
        var readOnly = StoreWrapper.OpenReadOnly(servingPath);
        lock (_lock)
        {
            _published[releaseId] = new PublishedEntry(ksContext, readOnly);
        }

        return new Release(releaseId, manifest.Version, ksContext, servingPath);
    }

    /// <summary>
    /// Record the owning KsContext into the artifact directory so publish
    /// (or a restart) can re-open the serving store without going back to
    /// the EF layer.
    /// </summary>
    internal static void WriteKsHeader(string artifactPath, KsContext ks)
    {
        var headerPath = Path.Combine(artifactPath, "ks.json");
        File.WriteAllText(headerPath,
            System.Text.Json.JsonSerializer.Serialize(new { ks.GraphIri, ks.BaseIri }));
    }

    private static KsContext? ReadKsHeader(string artifactPath)
    {
        var headerPath = Path.Combine(artifactPath, "ks.json");
        if (!File.Exists(headerPath)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(headerPath));
            var root = doc.RootElement;
            var graphIri = root.GetProperty("GraphIri").GetString() ?? "";
            var baseIri = root.GetProperty("BaseIri").GetString() ?? "";
            return new KsContext(graphIri, baseIri);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Read published
    // ------------------------------------------------------------------

    /// <summary>
    /// Read quads from the published serving store. Throws if the release
    /// has not been published.
    /// </summary>
    public IReadOnlyList<OntoQuad> ReadPublished(string releaseId, RdfLayer layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryGetPublishedEntry(releaseId, out var entry))
        {
            throw new InvalidOperationException($"Release '{releaseId}' is not published.");
        }
        var graphIri = GraphIriFor(entry.Ks, layer);
        return entry.Store.Match(graphIri: graphIri);
    }

    /// <summary>True once a release has been published and its serving store is open.</summary>
    public bool IsPublished(string releaseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        return TryGetPublishedEntry(releaseId, out _);
    }

    private bool TryGetPublishedEntry(string releaseId, out PublishedEntry entry)
    {
        lock (_lock)
        {
            if (_published.TryGetValue(releaseId, out entry!))
            {
                return true;
            }
        }

        // Lazy open: if a serving directory exists on disk we can re-open it
        // read-only without re-running publish. Useful for cross-process
        // restart scenarios. We need the KS to resolve the layer IRI; fall
        // back to the on-disk header.
        var servingPath = ServingPath(releaseId);
        if (Directory.Exists(servingPath))
        {
            try
            {
                var opened = StoreWrapper.OpenReadOnly(servingPath);
                var ks = ReadKsHeader(_artifacts.ReleasePath(releaseId))
                    ?? new KsContext(string.Empty, string.Empty);
                var fresh = new PublishedEntry(ks, opened);
                lock (_lock)
                {
                    if (!_published.TryGetValue(releaseId, out entry!))
                    {
                        _published[releaseId] = fresh;
                        entry = fresh;
                        return true;
                    }
                    opened.Dispose();
                    return _published.TryGetValue(releaseId, out entry!);
                }
            }
            catch
            {
                entry = null!;
                return false;
            }
        }
        entry = null!;
        return false;
    }

    // ------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------

    /// <summary>
    /// Delete a release: close its serving store, remove the artifact
    /// subdirectory, and free the version slot for reuse.
    /// </summary>
    public Task DeleteAsync(
        string releaseId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_published.TryGetValue(releaseId, out var entry))
            {
                entry.Store.Dispose();
                _published.Remove(releaseId);
            }
        }

        var servingPath = ServingPath(releaseId);
        if (Directory.Exists(servingPath))
        {
            Directory.Delete(servingPath, recursive: true);
        }

        _artifacts.Delete(releaseId);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Version allocation
    // ------------------------------------------------------------------

    /// <summary>
    /// Allocate the next free version string ("v1", "v2", …). Reuses
    /// numerically lowest freed slot so a delete-then-capture reuses v1.
    /// </summary>
    public string AllocateVersion()
    {
        var existing = _artifacts.ListVersions();
        var used = new HashSet<int>(existing.Count);
        foreach (var id in existing)
        {
            try
            {
                var m = _artifacts.LoadManifest(id);
                if (TryParseVersion(m.Version, out var n))
                    used.Add(n);
            }
            catch
            {
                // ignore unreadable manifests
            }
        }
        for (int i = 1; i < int.MaxValue; i++)
        {
            if (!used.Contains(i)) return $"v{i}";
        }
        throw new InvalidOperationException("No free version slots.");
    }

    /// <summary>List versions of captured releases (artifact-dir-derived).</summary>
    public IReadOnlyList<string> ListVersions() => _artifacts.ListVersions();

    private static bool TryParseVersion(string version, out int n)
    {
        n = 0;
        if (string.IsNullOrEmpty(version)) return false;
        if (!version.StartsWith("v", StringComparison.Ordinal)) return false;
        return int.TryParse(version.AsSpan(1), out n);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    internal static string GraphIriFor(KsContext ks, RdfLayer layer) => layer switch
    {
        RdfLayer.TBox => ks.TBoxGraph,
        RdfLayer.ABox => ks.ABoxGraph,
        RdfLayer.Vocabulary => ks.VocabularyGraph,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var e in _published.Values)
            {
                e.Store.Dispose();
            }
            _published.Clear();
        }
        _versionLock.Dispose();
    }
}