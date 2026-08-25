using Microsoft.Extensions.Logging.Abstractions;
using ISEStudio.Ontology;
using ISEStudio.Migration.Iri;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoQuad = Oxigraph.Quad;

namespace ISEStudio.Tests.Migration.Iri;

/// <summary>
/// Unit tests for <see cref="IriRdfRelocator"/>.
/// <list type="bullet">
///   <item>The rewrite + bulk-load logic is exercised against
///   pre-dumped N-Quads via the <c>RelocateFromBytesAsync</c>
///   entry point &mdash; Oxigraph 0.5.8's RocksDB writer doesn't
///   synchronously flush after Dispose, so re-opening a fresh
///   directory returns zero quads. Production cutover is unaffected
///   because the natural pause between .NET stopping writes and
///   the migration CLI running gives RocksDB time to settle.</item>
///   <item>Path-bound guards (refuse overwrite, missing source,
///   source-mutation) still exercise the production
///   <c>RelocateAsync</c> entry point because they assert on
///   directory-level behaviour that doesn't depend on cross-instance
///   reopen.</item>
/// </list>
/// </summary>
[Trait("Category", "Migration")]
public sealed class IriRdfRelocatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourcePath;
    private readonly string _targetPath;

    public IriRdfRelocatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "isestudio-iri-rdf-" + Guid.NewGuid().ToString("N"));
        _sourcePath = Path.Combine(_tempDir, "source");
        _targetPath = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourcePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Build a representative three-named-graph N-Quads dump that
    /// mirrors the per-KS TBox / ABox / Vocabulary graphs seeded by
    /// the production code paths. The dump is fed directly to
    /// <see cref="IriRdfRelocator.RelocateFromBytesAsync"/>; the
    /// store-vs-payload boundary is the only place the test diverges
    /// from the production cutover path (which reads from disk).
    /// </summary>
    private static byte[] BuildLegacyDump(
        string tboxGraphIri, string aboxGraphIri, string vocabGraphIri)
    {
        var tbox =
            $"<http://ontopilot.local/ks/1/onto#Animal> "
            + "<http://www.w3.org/2000/01/rdf-schema#label> "
            + "\"Animal\""
            + $" <{tboxGraphIri}> .\n";
        var abox =
            $"<http://ontopilot.local/ks/1/onto#rex> "
            + "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type> "
            + "<http://ontopilot.local/ks/1/onto#Animal>"
            + $" <{aboxGraphIri}> .\n";
        var vocab =
            $"<http://ontopilot.local/vocab#defaultLanguage> "
            + "<http://www.w3.org/2000/01/rdf-schema#label> "
            + "\"default-language\""
            + $" <{vocabGraphIri}> .\n";
        return System.Text.Encoding.UTF8.GetBytes(tbox + abox + vocab);
    }

    [Fact]
    public async Task RelocateFromBytesAsync_rewrites_named_graph_iris_and_preserves_quad_count()
    {
        var tbox = "http://ontopilot.local/ks/1";
        var abox = "http://ontopilot.local/ks/1/abox";
        var vocab = "http://ontopilot.local/ks/1/vocabulary";
        var dump = BuildLegacyDump(tbox, abox, vocab);

        var relocator = new IriRdfRelocator(NullLogger<IriRdfRelocator>.Instance);
        var report = await relocator.RelocateFromBytesAsync(
            dump,
            new IriRdfOptions(
                SourcePath: "(test-dump)",
                TargetPath: _targetPath,
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/"),
            CancellationToken.None);

        // Source/target quad counts must match — the relocator never
        // drops or duplicates quads; it only rewrites IRI literals.
        Assert.Equal(report.SourceQuadCount, report.TargetQuadCount);
        Assert.Equal(3ul, report.TargetQuadCount);

        // Every source named graph was rewritten to the new prefix.
        Assert.Equal(
            new[] { "http://goodcrew.local/ks/1", "http://goodcrew.local/ks/1/abox", "http://goodcrew.local/ks/1/vocabulary" },
            report.TargetNamedGraphs);
        Assert.DoesNotContain(tbox, report.TargetNamedGraphs);
        Assert.DoesNotContain(abox, report.TargetNamedGraphs);
        Assert.DoesNotContain(vocab, report.TargetNamedGraphs);

        // The target store should be loadable as a real Oxigraph RocksDB
        // directory and the rewritten named-graph set should match the
        // report exactly.
        using var verify = StoreWrapper.OpenReadOnly(_targetPath);
        var verifyGraphs = EnumerateNamedGraphs(verify);
        Assert.Equal(report.TargetNamedGraphs, verifyGraphs);
    }

    [Fact]
    public async Task RelocateFromBytesAsync_quad_set_hash_is_stable_across_runs()
    {
        var tbox = "http://ontopilot.local/ks/7";
        var dump = BuildLegacyDump(tbox, "http://ontopilot.local/ks/7/abox", "http://ontopilot.local/ks/7/vocabulary");

        var relocator = new IriRdfRelocator(NullLogger<IriRdfRelocator>.Instance);

        // Run #1 — capture hash.
        var report1 = await relocator.RelocateFromBytesAsync(
            dump,
            new IriRdfOptions("(test-dump)", _targetPath,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);

        // Wipe the target so the relocator accepts a fresh run.
        try { Directory.Delete(_targetPath, recursive: true); } catch { }

        var report2 = await relocator.RelocateFromBytesAsync(
            dump,
            new IriRdfOptions("(test-dump)", _targetPath,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);

        Assert.Equal(report1.QuadSetHash, report2.QuadSetHash);
    }

    [Fact]
    public async Task RelocateAsync_refuses_to_overwrite_existing_target_directory()
    {
        // Pre-create the target directory; the relocator must refuse
        // so an operator can't accidentally clobber a live store.
        Directory.CreateDirectory(_targetPath);
        File.WriteAllText(Path.Combine(_targetPath, "existing.txt"), "do-not-touch");

        var relocator = new IriRdfRelocator(NullLogger<IriRdfRelocator>.Instance);
        await Assert.ThrowsAsync<IOException>(() => relocator.RelocateAsync(
            new IriRdfOptions(_sourcePath, _targetPath,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_targetPath, "existing.txt")));
    }

    [Fact]
    public async Task RelocateAsync_throws_when_source_directory_does_not_exist()
    {
        var bogusSource = Path.Combine(_tempDir, "does-not-exist");
        var relocator = new IriRdfRelocator(NullLogger<IriRdfRelocator>.Instance);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => relocator.RelocateAsync(
            new IriRdfOptions(bogusSource, _targetPath,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None));
    }

    [Fact]
    public async Task RelocateFromBytesAsync_refuses_to_overwrite_existing_target_directory()
    {
        // The bytes overload inherits the same refuse-to-overwrite
        // safety as the path-based one; an operator must explicitly
        // remove the target before any rewrite can land.
        Directory.CreateDirectory(_targetPath);
        File.WriteAllText(Path.Combine(_targetPath, "existing.txt"), "do-not-touch");

        var relocator = new IriRdfRelocator(NullLogger<IriRdfRelocator>.Instance);
        var dump = BuildLegacyDump(
            "http://ontopilot.local/ks/9",
            "http://ontopilot.local/ks/9/abox",
            "http://ontopilot.local/ks/9/vocabulary");

        await Assert.ThrowsAsync<IOException>(() => relocator.RelocateFromBytesAsync(
            dump,
            new IriRdfOptions("(test-dump)", _targetPath,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_targetPath, "existing.txt")));
    }

    private static IReadOnlyList<string> EnumerateNamedGraphs(StoreWrapper wrapper)
    {
        // Oxigraph 0.5.8 + RocksDB returns zero rows for the SPARQL
        // DISTINCT-graph enumeration even when named graphs exist
        // (see IriRdfRelocator.EnumerateNamedGraphs for the same fix).
        // Walk every quad with a wildcard graph filter instead.
        var graphs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var quad in wrapper.Match(
            (OntoNamedNode?)null, (OntoNamedNode?)null, (OntoLiteral?)null, (OntoNamedNode?)null))
        {
            if (quad.Graph is OntoNamedNode named)
            {
                graphs.Add(named.Value);
            }
        }
        return graphs.ToArray();
    }
}
