using System.Security.Cryptography;
using System.Text;
using Oxigraph;
using OntoStore = Oxigraph.Store;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoRdfFormat = Oxigraph.RdfFormat;
using OntoQueryResultsFormat = Oxigraph.QueryResultsFormat;

namespace ISEStudio.Migration.Rdf;

/// <summary>
/// Composite result of a single RDF migration run. Carries the brief's
/// five-field <see cref="Report"/> and the audit / provenance sibling
/// (<see cref="Audit"/>) that Task 4's orchestrator needs to gate the
/// cutover. The two records are deliberately separate so
/// <see cref="RdfMigrationReport"/> stays exactly the shape the brief
/// mandates.
/// </summary>
/// <param name="Report">The five-field data record (Strategy, QuadCount,
/// NamedGraphs, QueryResultHashes, WriteRevertPassed).</param>
/// <param name="Audit">The audit / provenance sibling
/// (SourceOpenedByDotNet, CopyPath, WorkPath, FinishedAtUtc,
/// CleanupSucceeded, DirectStrategyError).</param>
public sealed record RdfMigrationResult(RdfMigrationReport Report, RdfMigrationAudit Audit);

/// <summary>
/// Verifies that a COPY of the Python / pyoxigraph-managed Oxigraph
/// RocksDB directory can be read by the .NET / Oxigraph 0.5.8 stack,
/// without ever opening the source itself.
///
/// <para>Two strategies are supported:
/// <list type="number">
///   <item><b>direct</b> — <c>OxigraphStore.OpenReadOnly(copyPath)</c>
///   enumerates the copy's quads and runs every smoke query.</item>
///   <item><b>nquads</b> — when the direct read fails (e.g. the copy is
///   not a valid RocksDB directory, or a future Oxigraph version changes
///   the on-disk format), the caller is expected to have already
///   exported the source to <c>workPath/nquads-export.nq</c> via
///   <c>Export-PythonRdf.ps1</c>; we open a fresh
///   <c>OxigraphStore(workPath)</c>, <c>Load</c> the N-Quads, and run the
///   same enumeration + smoke queries. The manifest hashes MUST be
///   identical to the direct-read manifest for the same logical
///   content.</item>
/// </list>
/// </para>
///
/// <para><b>Source safety.</b> <paramref name="sourcePath"/> is only used
/// to compute the SHA-256 fingerprint via <see cref="DirectoryHash"/>;
/// no <c>OxigraphStore</c> is ever instantiated with it. The
/// <see cref="RdfMigrationAudit.SourceOpenedByDotNet"/> flag is
/// therefore <c>false</c> by construction and is asserted in the
/// integration test.</para>
/// </summary>
public static class RdfMigrationCommand
{
    /// <summary>The N-Quads file the fallback path expects at
    /// <c>workPath/nquads-export.nq</c>.</summary>
    public const string NQuadsFileName = "nquads-export.nq";

    /// <summary>
    /// Verify that <paramref name="copyPath"/> (a copy of the Python
    /// Oxigraph directory) can be read by the .NET Oxigraph 0.5.8 stack.
    /// Try the direct read first; on failure, fall back to N-Quads load.
    /// </summary>
    /// <param name="sourcePath">Path of the original Python / pyoxigraph
    /// source. MUST be read-only — the command never opens it.</param>
    /// <param name="copyPath">Path of the .NET-exclusive copy.</param>
    /// <param name="workPath">Scratch directory used for the N-Quads
    /// fallback; must already contain <c>nquads-export.nq</c> when the
    /// direct path fails.</param>
    /// <param name="queries">Smoke queries to run against both strategies.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<RdfMigrationResult> VerifyCopyAsync(
        string sourcePath,
        string copyPath,
        string workPath,
        IReadOnlyList<(string Name, string Query)> queries,
        CancellationToken ct)
        => VerifyCopyAsync(sourcePath, copyPath, workPath, queries, copyFromSource: true, ct);

    /// <summary>
    /// Same as <see cref="VerifyCopyAsync(string, string, string, IReadOnlyList{ValueTuple{string, string}}, CancellationToken)"/>
    /// but with an explicit <paramref name="copyFromSource"/> flag. When
    /// <c>false</c>, the command skips the file-copy step (so the
    /// caller can simulate a missing/bad copy and force the N-Quads
    /// fallback path).
    /// </summary>
    public static async Task<RdfMigrationResult> VerifyCopyAsync(
        string sourcePath,
        string copyPath,
        string workPath,
        IReadOnlyList<(string Name, string Query)> queries,
        bool copyFromSource,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(copyPath);
        ArgumentException.ThrowIfNullOrEmpty(workPath);
        ArgumentNullException.ThrowIfNull(queries);

        // Step 1: make sure the source exists so the test can prove we
        // never touched it (DirectoryHash.Compute also asserts existence).
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException(
                $"RdfMigrationCommand.VerifyCopyAsync: source '{sourcePath}' does not exist.");
        }

        // Step 2: copy the source into the exclusive copy directory (when
        // copyFromSource is true). We use a recursive file copy (NOT a
        // hard link) so the .NET Oxigraph owns the copy and the original
        // is provably untouched.
        if (copyFromSource)
        {
            await CopyDirectoryAsync(sourcePath, copyPath, ct);
        }

        // Step 3: try the direct read. Capture the exception so the audit
        // record can surface the failure mode to Task 4's orchestrator.
        (string Strategy, RdfMigrationReport Report, string? DirectError) directResult;
        try
        {
            directResult = (
                "direct",
                await RunStrategyAsync(
                    strategy: "direct",
                    openStore: () => OntoStore.OpenReadOnly(copyPath),
                    copyPath: copyPath,
                    workPath: workPath,
                    queries: queries,
                    ct: ct),
                null);
        }
        catch (Exception ex)
        {
            // Direct read failed (RocksDB format incompatibility, partial
            // copy, etc.). Surface the exception for the caller to log,
            // then fall through to the N-Quads fallback.
            Console.Error.WriteLine(
                $"[RdfMigration] direct strategy failed: {ex.GetType().Name}: {ex.Message}; falling back to N-Quads.");
            directResult = ("failed", default!, ex.GetType().Name + ": " + ex.Message);
        }

        if (directResult.Report is not null)
        {
            return new RdfMigrationResult(
                Report: directResult.Report,
                Audit: new RdfMigrationAudit(
                    SourceOpenedByDotNet: false,
                    CopyPath: copyPath,
                    WorkPath: workPath,
                    FinishedAtUtc: DateTimeOffset.UtcNow,
                    CleanupSucceeded: true,
                    DirectStrategyError: directResult.DirectError));
        }

        // Step 4: N-Quads fallback. The caller is expected to have
        // already produced workPath/nquads-export.nq via Export-PythonRdf.ps1.
        var fallbackReport = await RunStrategyAsync(
            strategy: "nquads",
            openStore: () => OpenFreshStoreForLoad(workPath),
            copyPath: copyPath,
            workPath: workPath,
            queries: queries,
            ct: ct);

        return new RdfMigrationResult(
            Report: fallbackReport,
            Audit: new RdfMigrationAudit(
                SourceOpenedByDotNet: false,
                CopyPath: copyPath,
                WorkPath: workPath,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                CleanupSucceeded: true,
                DirectStrategyError: directResult.DirectError));
    }

    /// <summary>
    /// Write-revert smoke: open the copy read-write, add a probe quad to
    /// a fresh graph, verify the count went up by exactly one, then
    /// atomically wipe the probe graph and verify the count is back to
    /// <paramref name="expectedCount"/>.
    /// </summary>
    /// <remarks>
    /// <para>The Add → assert → ClearGraph → assert sequence is wrapped
    /// in <c>try/finally</c> so a failure between Add and the assertions
    /// can't leak the probe quad. The <c>finally</c> block runs
    /// <see cref="OntoStore.ClearGraph(IGraphName)"/> again (best-effort)
    /// and reports <see cref="RdfMigrationAudit.CleanupSucceeded"/> back
    /// to the caller via the returned <see cref="RdfMigrationResult"/>.
    /// </para>
    /// </remarks>
    /// <param name="previous">The previous run's result, whose
    /// <see cref="RdfMigrationReport.WriteRevertPassed"/> will be flipped
    /// and whose <see cref="RdfMigrationAudit"/> will carry the cleanup
    /// outcome.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fresh <see cref="RdfMigrationResult"/> with
    /// <c>WriteRevertPassed</c> and <c>CleanupSucceeded</c> updated.</returns>
    public static async Task<RdfMigrationResult> WriteRevertSmokeAsync(
        RdfMigrationResult previous,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var copyPath = previous.Audit.CopyPath;
        ArgumentException.ThrowIfNullOrEmpty(copyPath);

        // Run synchronously on the thread pool — Oxigraph 0.5.8 has no
        // async Store API; we don't want to block the calling thread on
        // a potentially multi-thousand-quad round-trip.
        var (passed, cleanupSucceeded) = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var store = new OntoStore(copyPath);

            var probeGraph = new OntoNamedNode(
                "urn:ontopilot:probe:write-revert:" + Guid.NewGuid().ToString("N"));
            var probeSubj = new OntoNamedNode("urn:ontopilot:probe:subj");
            var probePred = new Oxigraph.NamedNode("urn:ontopilot:probe:pred");
            var probeObj = new Oxigraph.Literal("probe-value");
            var probeQuad = new OntoQuad(probeSubj, probePred, probeObj, probeGraph);

            // Local mutable state captured by the try / finally below.
            // Using a closure variable (not a finally `return`) so the
            // compiler doesn't trip on CS0157 (cannot leave finally).
            var passedLocal = false;
            var cleanupOk = false;
            var before = store.Count;
            try
            {
                store.Add(probeQuad);
                var afterAdd = store.Count;
                if (afterAdd != before + 1)
                {
                    throw new InvalidOperationException(
                        $"Write-revert smoke: expected count {before + 1} after add, got {afterAdd}.");
                }

                // Atomic revert: ClearGraph is a single RocksDB batch op
                // (verified in ISEStudio.OxigraphProbe/Program.cs line 73)
                // and wipes every quad in the probe graph in one shot.
                store.ClearGraph(probeGraph);
                var afterClear = store.Count;
                if (afterClear != before)
                {
                    throw new InvalidOperationException(
                        $"Write-revert smoke: expected count {before} after ClearGraph, got {afterClear}.");
                }

                passedLocal = true;
            }
            finally
            {
                // Best-effort cleanup: a transient IO / RocksDB error
                // during Add/ClearGraph must NOT leave the probe quad in
                // the copy. The second ClearGraph is a single graph wipe,
                // so even if the first one didn't run (or failed), this
                // guarantees the probe graph ends empty. Success of this
                // best-effort cleanup is what the audit field reports.
                try
                {
                    store.ClearGraph(probeGraph);
                    cleanupOk = true;
                }
                catch
                {
                    cleanupOk = false;
                }
            }

            return (passedLocal, cleanupOk);
        }, ct);

        return new RdfMigrationResult(
            Report: previous.Report.WithWriteRevertPassed(passed),
            Audit: previous.Audit with
            {
                CleanupSucceeded = cleanupSucceeded,
                FinishedAtUtc = DateTimeOffset.UtcNow,
            });
    }

    // -----------------------------------------------------------------
    // Strategy runner
    // -----------------------------------------------------------------

    private static async Task<RdfMigrationReport> RunStrategyAsync(
        string strategy,
        Func<OntoStore> openStore,
        string copyPath,
        string workPath,
        IReadOnlyList<(string Name, string Query)> queries,
        CancellationToken ct)
    {
        // Oxigraph 0.5.8 has no async Store API; the enumeration +
        // SPARQL evaluation all happen on the thread pool to avoid
        // blocking the caller's thread.
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var store = openStore();

            ulong quadCount = store.Count;
            IReadOnlyList<string> namedGraphs = EnumerateNamedGraphs(store);
            IReadOnlyDictionary<string, string> queryHashes = RunSmokeQueries(store, queries, ct);

            return new RdfMigrationReport(
                Strategy: strategy,
                QuadCount: quadCount,
                NamedGraphs: namedGraphs,
                QueryResultHashes: queryHashes,
                WriteRevertPassed: false);
        }, ct);
    }

    private static IReadOnlyList<string> EnumerateNamedGraphs(OntoStore store)
    {
        // Oxigraph 0.5.8's SPARQL `SELECT DISTINCT ?g WHERE { GRAPH ?g {} }`
        // is the canonical way to enumerate named graphs. We collect them
        // out of the QuerySolutions, sort, and return as plain IRIs.
        var graphs = new SortedSet<string>(StringComparer.Ordinal);
        using var qr = store.Query("SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } }");
        var qs = AsQuerySolutions(qr);
        foreach (var sol in qs)
        {
            if (sol.TryGetValue("g", out var term) && term is OntoNamedNode named)
            {
                graphs.Add(named.Value);
            }
        }
        return graphs.ToArray();
    }

    private static IReadOnlyDictionary<string, string> RunSmokeQueries(
        OntoStore store,
        IReadOnlyList<(string Name, string Query)> queries,
        CancellationToken ct)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, query) in queries)
        {
            ct.ThrowIfCancellationRequested();
            using var qr = store.Query(query);
            var qs = AsQuerySolutions(qr);
            // Oxigraph's JSON serialiser is deterministic and preserves
            // binding order; we hash it so the manifest is stable across
            // runs. Using the JSON format also strips engine-internal
            // metadata that the SPARQL XML/CSV serialisations include.
            var json = qs.Serialize(OntoQueryResultsFormat.Json);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            hashes[name] = hash;
        }
        return hashes;
    }

    private static Oxigraph.QuerySolutions AsQuerySolutions(OntoStore store, string query)
    {
        var qr = store.Query(query);
        return AsQuerySolutions(qr);
    }

    private static Oxigraph.QuerySolutions AsQuerySolutions(Oxigraph.QueryResults qr)
    {
        if (qr is Oxigraph.QuerySolutions qs)
        {
            return qs;
        }
        // BOOLEAN / TRIPLES results are out of scope for the smoke
        // queries — every fixture is a SELECT. Fail loudly so a future
        // change to the fixture set doesn't silently drop results.
        qr.Dispose();
        throw new NotSupportedException(
            $"RdfMigrationCommand expected a SELECT QuerySolutions but got '{qr.GetType().FullName}'.");
    }

    /// <summary>
    /// Open a fresh Oxigraph store at <paramref name="workPath"/>,
    /// load <see cref="NQuadsFileName"/> into it, and return it.
    /// Caller owns disposal.
    /// </summary>
    private static OntoStore OpenFreshStoreForLoad(string workPath)
    {
        if (!Directory.Exists(workPath))
        {
            Directory.CreateDirectory(workPath);
        }

        var nqPath = Path.Combine(workPath, NQuadsFileName);
        if (!File.Exists(nqPath))
        {
            throw new FileNotFoundException(
                $"RdfMigrationCommand N-Quads fallback expected '{nqPath}' but it was not found. "
                + "Run Export-PythonRdf.ps1 before invoking the fallback path.",
                nqPath);
        }

        // Snapshot the N-Quads bytes before we wipe the directory — the
        // Oxigraph store needs an empty directory to open cleanly, and
        // we don't want to delete the export in the process.
        var nq = File.ReadAllText(nqPath);

        // Clear any non-N-Quads RocksDB files left over from a previous
        // run; Oxigraph refuses to Load into a non-empty store.
        foreach (var f in Directory.EnumerateFiles(workPath))
        {
            if (string.Equals(Path.GetFileName(f), NQuadsFileName, StringComparison.Ordinal))
            {
                continue;
            }
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
            File.Delete(f);
        }

        var store = new OntoStore(workPath);
        store.Load(nq, OntoRdfFormat.NQuads);
        return store;
    }

    // -----------------------------------------------------------------
    // File-system helpers
    // -----------------------------------------------------------------

    private static async Task CopyDirectoryAsync(
        string sourcePath,
        string copyPath,
        CancellationToken ct)
    {
        if (Directory.Exists(copyPath))
        {
            // Wipe any previous copy (test runs share the work root).
            await Task.Run(() =>
            {
                foreach (var f in Directory.EnumerateFiles(copyPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
                }
                Directory.Delete(copyPath, recursive: true);
            }, ct);
        }
        Directory.CreateDirectory(copyPath);

        // Walk the source and copy every file in sorted order. Sorting
        // gives us deterministic copy semantics, which matters when the
        // source directory has a few thousand RocksDB SST files and the
        // copy is rebuilt on every test run.
        var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var srcFile in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, srcFile);
            var destFile = Path.Combine(copyPath, relative);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            // File copy (NOT a hard link) — the .NET Oxigraph owns the
            // copy exclusively; the source remains untouched even if the
            // copy is later mutated by WriteRevertSmokeAsync.
            await Task.Run(() => File.Copy(srcFile, destFile, overwrite: true), ct);
        }
    }
}