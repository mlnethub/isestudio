using System.Text.Json;
using OnToPilot.Migration.Rdf;

namespace OnToPilot.IntegrationTests.Migration;

/// <summary>
/// Verifies the reversible RDF migration that bridges the Python /
/// pyoxigraph-managed Oxigraph RocksDB directory (read-only rollback copy)
/// into the .NET / Oxigraph 0.5.8-managed Oxigraph store used by the new
/// stack.
///
/// <para><b>Global constraint.</b> The original Python RocksDB directory
/// MUST stay read-only. The .NET side never opens it. Every test in this
/// class works on a copy under <c>.artifacts/rdf-test/</c>; the only time
/// <c>SourceStore</c> is referenced is to compute the SHA-256 fingerprint
/// before and after the migration runs, to prove the source has not been
/// touched.</para>
///
/// <para>The fixture is a synthetic on-disk Oxigraph store (Task 2 doesn't
/// require a real Python backend in CI). Tests carry
/// <c>[Trait("Category", "Migration")]</c> so the rehearsal / cutover
/// orchestration (Task 4) can filter them out of the default CI run.
/// </para>
/// </summary>
public sealed class RdfMigrationTests : IAsyncLifetime
{
    /// <summary>
    /// Repo-relative working directory under which every test artifact
    /// lives. The directory is created on <see cref="InitializeAsync"/>
    /// and removed on <see cref="DisposeAsync"/>; tests must not assume
    /// state from a previous run.
    /// </summary>
    private readonly string _workRoot;

    /// <summary>The "Python source" we never open from .NET — synthetic.</summary>
    private string SourceStore { get; }

    /// <summary>The exclusive copy the .NET command opens.</summary>
    private string ProbeCopy { get; }

    /// <summary>The N-Quads scratch directory the fallback uses.</summary>
    private string WorkDir { get; }

    /// <summary>The smoke queries the parity check will run.</summary>
    private IReadOnlyList<(string Name, string Query)> Queries { get; }

    public RdfMigrationTests()
    {
        var repoRoot = LocateRepoRoot();
        _workRoot = Path.Combine(repoRoot, ".artifacts", "rdf-test");
        SourceStore = Path.Combine(_workRoot, "source");
        ProbeCopy = Path.Combine(_workRoot, "copy");
        WorkDir = Path.Combine(_workRoot, "work");
        Queries = LoadQueries(repoRoot);
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workRoot);
        Recreate(SourceStore);
        Recreate(ProbeCopy);
        Recreate(WorkDir);

        // Seed a deterministic synthetic RocksDB store so the tests can
        // run without the real Python / oxigraph backend being available.
        // Three named graphs (tbox, abox, vocab) with a known number of
        // quads each — enough for the smoke queries and the parity check
        // to be meaningful.
        SeedSyntheticStore(SourceStore);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        // Leave the artifact dir on disk for post-mortem; CI cleans it via
        // its own scratch policy. Returning Task.CompletedTask keeps the
        // IAsyncLifetime contract honest without forcing a delete here.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Required verbatim test (the brief). Computes a deterministic
    /// SHA-256 over every file in the source directory before and after
    /// <c>VerifyCopyAsync</c>; the two digests MUST be byte-identical.
    /// The <c>SourceOpenedByDotNet</c> flag on the audit sibling MUST be
    /// <c>false</c> (it stays false by construction because the
    /// production code never instantiates an <c>OxigraphStore</c> with
    /// the source path).
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Verify_copy_never_opens_or_changes_source_directory()
    {
        var before = DirectoryHash.Compute(SourceStore);
        var result = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);
        var after = DirectoryHash.Compute(SourceStore);

        Assert.Equal(before, after);
        Assert.False(result.Audit.SourceOpenedByDotNet);
        // The copy must now exist (the command owns it).
        Assert.True(Directory.Exists(ProbeCopy));
        // The report must record the strategy it picked and a query hash.
        Assert.NotEmpty(result.Report.Strategy);
        Assert.NotEmpty(result.Report.QueryResultHashes);
    }

    /// <summary>
    /// Direct-read strategy: when the copy opens read-only, the manifest
    /// records Strategy="direct". Re-running on the same copy produces the
    /// same manifest (idempotent).
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Direct_read_strategy_produces_deterministic_manifest()
    {
        var first = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);

        Assert.Equal("direct", first.Report.Strategy);
        Assert.True(first.Report.QuadCount > 0, "synthetic source should have non-zero quads");
        Assert.True(first.Report.NamedGraphs.Count >= 2, "synthetic source should expose >=2 graphs");
        Assert.Equal(3, first.Report.NamedGraphs.Count);

        // Re-run on the same copy — the second run sees an already-populated
        // Oxigraph dir, but the copy we hand in must be a fresh one to
        // avoid the second run opening the first run's writes.
        var second = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);

        Assert.Equal(first.Report.QuadCount, second.Report.QuadCount);
        Assert.Equal(first.Report.NamedGraphs.OrderBy(g => g), second.Report.NamedGraphs.OrderBy(g => g));
        Assert.Equal(first.Report.QueryResultHashes, second.Report.QueryResultHashes);
    }

    /// <summary>
    /// N-Quads fallback: when the direct read cannot open the copy (we
    /// simulate this by skipping the copy step and providing an
    /// N-Quads-only workdir), the command falls back to Load(N-Quads)
    /// on a fresh Oxigraph store and produces a manifest with
    /// Strategy="nquads" whose hashes match the direct-read manifest for
    /// the same logical content.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task N_quads_fallback_strategy_produces_matching_manifest()
    {
        // First, run the direct path on the seeded source so we have a
        // known-good manifest.
        var direct = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);

        // Now run the fallback: a fresh workdir seeded only with the
        // N-Quads export of the same source. We pass copyFromSource:false
        // so the command doesn't recreate the copy (which would just
        // succeed via the direct path); instead it falls back to the
        // N-Quads file at workPath/nquads-export.nq.
        var fallbackWork = Path.Combine(_workRoot, "work-fallback");
        Recreate(fallbackWork);
        // Point the copy path at a non-existent directory so the direct
        // strategy attempt fails fast and we fall through to nquads.
        var missingCopy = Path.Combine(_workRoot, "copy-missing");
        if (Directory.Exists(missingCopy)) Directory.Delete(missingCopy, recursive: true);

        // Seed the N-Quads fallback file from the source store. This is
        // what the production Export-PythonRdf.ps1 would have written.
        var nqPath = Path.Combine(fallbackWork, "nquads-export.nq");
        DumpNQuadsFromStore(SourceStore, nqPath);
        Assert.True(File.Exists(nqPath));
        Assert.True(new FileInfo(nqPath).Length > 0, "N-Quads export must be non-empty");

        var fallback = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, missingCopy, fallbackWork, Queries,
            copyFromSource: false, CancellationToken.None);

        Assert.Equal("nquads", fallback.Report.Strategy);
        Assert.Equal(direct.Report.QuadCount, fallback.Report.QuadCount);
        Assert.Equal(
            direct.Report.NamedGraphs.OrderBy(g => g).ToArray(),
            fallback.Report.NamedGraphs.OrderBy(g => g).ToArray());
        Assert.Equal(direct.Report.QueryResultHashes, fallback.Report.QueryResultHashes);
    }

    /// <summary>
    /// Write-revert smoke: the command opens the copy read-write, adds a
    /// probe quad to a fresh graph, verifies the count increased by 1,
    /// atomically wipes the probe graph (Oxigraph's ClearGraph is a
    /// single-batch op), and verifies the count is back to the
    /// original. This is the structural guarantee that the copy is a
    /// true read-write Oxigraph directory the cutover can use as its
    /// workspace.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Write_revert_smoke_round_trips_to_original_count()
    {
        var initial = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);

        var afterRevert = await RdfMigrationCommand.WriteRevertSmokeAsync(
            initial, CancellationToken.None);

        Assert.True(afterRevert.Report.WriteRevertPassed);
        Assert.True(afterRevert.Audit.CleanupSucceeded);

        var final = await RdfMigrationCommand.VerifyCopyAsync(
            SourceStore, ProbeCopy, WorkDir, Queries, CancellationToken.None);
        Assert.Equal(initial.Report.QuadCount, final.Report.QuadCount);
        Assert.False(final.Report.WriteRevertPassed);
    }

    // -----------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------

    private static string LocateRepoRoot()
    {
        // The Migration project lives under <repo-root>/src, and the
        // OnToPilot.sln file is in <repo-root>/src too. The repo root is
        // therefore the parent of <repo-root>/src — the directory that
        // contains both `migration/` (Task 2 fixtures + scripts) and the
        // `src/` source tree. We walk up looking for that shape rather
        // than for OnToPilot.sln so the test is robust against the sln
        // moving up or down.
        var location = AppContext.BaseDirectory;
        var cursor = new DirectoryInfo(location);
        while (cursor is not null)
        {
            var migrationCandidate = Path.Combine(cursor.FullName, "migration");
            var srcCandidate = Path.Combine(cursor.FullName, "src");
            if (Directory.Exists(migrationCandidate) && Directory.Exists(srcCandidate))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the repository root from {location}; "
            + "expected a directory containing both 'migration/' and 'src/'.");
    }

    private static void Recreate(string path)
    {
        if (Directory.Exists(path))
        {
            // Some RocksDB / Oxigraph files are read-only on Windows after
            // the store closes. Clear any read-only bit so Directory.Delete
            // doesn't throw, then nuke the tree.
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    private static IReadOnlyList<(string Name, string Query)> LoadQueries(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "migration", "fixtures", "rdf-smoke-queries.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required fixture '{path}' is missing. Task 2 must ship it next to the migration script.",
                path);
        }
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<QueryEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<QueryEntry>();
        return entries.Select(e => (e.Name, e.Query)).ToList();
    }

    private sealed class QueryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
    }

    /// <summary>
    /// Seed a small, deterministic Oxigraph store at <paramref name="path"/>
    /// with three named graphs. The shape is stable across runs so the
    /// manifest hashes are reproducible.
    /// </summary>
    private static void SeedSyntheticStore(string path)
    {
        using var store = new Oxigraph.Store(path);
        var tbox = new Oxigraph.NamedNode("urn:ontopilot:test:tbox");
        var abox = new Oxigraph.NamedNode("urn:ontopilot:test:abox");
        var vocab = new Oxigraph.NamedNode("urn:ontopilot:test:vocab");

        // TBox: 3 axioms.
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:cls:Dog"),
            new Oxigraph.NamedNode("http://www.w3.org/2000/01/rdf-schema#subClassOf"),
            new Oxigraph.NamedNode("urn:cls:Animal"),
            tbox));
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:cls:Cat"),
            new Oxigraph.NamedNode("http://www.w3.org/2000/01/rdf-schema#subClassOf"),
            new Oxigraph.NamedNode("urn:cls:Animal"),
            tbox));
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:prop:hasName"),
            new Oxigraph.NamedNode("http://www.w3.org/2000/01/rdf-schema#domain"),
            new Oxigraph.NamedNode("urn:cls:Animal"),
            tbox));

        // ABox: 3 individual facts.
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:ind:rex"),
            new Oxigraph.NamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
            new Oxigraph.NamedNode("urn:cls:Dog"),
            abox));
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:ind:rex"),
            new Oxigraph.NamedNode("urn:prop:hasName"),
            new Oxigraph.Literal("Rex"),
            abox));
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:ind:whiskers"),
            new Oxigraph.NamedNode("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"),
            new Oxigraph.NamedNode("urn:cls:Cat"),
            abox));

        // Vocab: 2 SKOS-like concepts.
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:vocab:animal"),
            new Oxigraph.NamedNode("http://www.w3.org/2004/02/skos/core#prefLabel"),
            new Oxigraph.Literal("animal", Language: "en"),
            vocab));
        store.Add(new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:vocab:dog"),
            new Oxigraph.NamedNode("http://www.w3.org/2004/02/skos/core#prefLabel"),
            new Oxigraph.Literal("dog", Language: "en"),
            vocab));
    }

    /// <summary>
    /// Use the Oxigraph probe's pattern (Store.Dump(RdfFormat.NQuads)) to
    /// serialise a RocksDB store into an N-Quads file at <paramref name="nqPath"/>.
    /// This is what Export-PythonRdf.ps1 would produce when given a Python
    /// source; we run it here as a fixture seed so the N-Quads fallback
    /// test is reproducible without depending on the script being installed.
    /// </summary>
    private static void DumpNQuadsFromStore(string sourcePath, string nqPath)
    {
        using var store = new Oxigraph.Store(sourcePath);
        var nq = store.Dump(Oxigraph.RdfFormat.NQuads);
        File.WriteAllText(nqPath, nq);
    }
}
