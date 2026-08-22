using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OnToPilot.Migration.Iri;
using OnToPilot.Ontology;

namespace OnToPilot.Tests.Migration.Iri;

/// <summary>
/// Unit tests for <see cref="IriShardRewriter"/>.
/// <list type="bullet">
///   <item>Each test materialises a release directory (three N-Quads
///   shards + manifest + ks.json) or an export directory (one shard
///   + manifest) on a temp filesystem, runs the rewriter, and
///   asserts on the rewritten content + SHA-256.</item>
///   <item>Dry-run mode is asserted to be a no-op (no bytes on disk
///   changed) so the cutover gate can preview the blast radius
///   safely.</item>
/// </list>
/// </summary>
[Trait("Category", "Migration")]
public sealed class IriShardRewriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _releasesRoot;
    private readonly string _exportsRoot;

    public IriShardRewriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ontopilot-iri-shards-" + Guid.NewGuid().ToString("N"));
        _releasesRoot = Path.Combine(_tempDir, "releases");
        _exportsRoot = Path.Combine(_tempDir, "exports");
        Directory.CreateDirectory(_releasesRoot);
        Directory.CreateDirectory(_exportsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Materialise a single release directory with three N-Quads shards
    /// (each carrying legacy-prefix IRIs), a ks.json header, and a
    /// manifest with the corresponding SHA-256 entries.
    /// </summary>
    private string SeedRelease(string releaseId, string tboxGraphIri, string baseIri)
    {
        var dir = Path.Combine(_releasesRoot, releaseId);
        Directory.CreateDirectory(dir);

        var tboxNq = $@"<http://ontopilot.local/ks/1/onto#Animal> <http://www.w3.org/2000/01/rdf-schema#label> ""Animal"" <{tboxGraphIri}> .
";
        var vocabNq = $@"<http://ontopilot.local/vocab#defaultLanguage> <http://www.w3.org/2000/01/rdf-schema#label> ""default-language"" <{tboxGraphIri}> .
";
        var aboxNq = $@"<http://ontopilot.local/ks/1/onto#rex> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://ontopilot.local/ks/1/onto#Animal> <{tboxGraphIri}/abox> .
";
        File.WriteAllText(Path.Combine(dir, "tbox.nq"), tboxNq);
        File.WriteAllText(Path.Combine(dir, "vocabulary.nq"), vocabNq);
        File.WriteAllText(Path.Combine(dir, "abox.nq"), aboxNq);

        File.WriteAllText(
            Path.Combine(dir, "ks.json"),
            JsonSerializer.Serialize(new { GraphIri = tboxGraphIri, BaseIri = baseIri }));

        var tboxBytes = System.Text.Encoding.UTF8.GetBytes(tboxNq);
        var vocabBytes = System.Text.Encoding.UTF8.GetBytes(vocabNq);
        var aboxBytes = System.Text.Encoding.UTF8.GetBytes(aboxNq);
        var manifest = new ReleaseManifest(
            Version: "v1",
            Files: new[]
            {
                new ReleaseFileManifest("tbox", "tbox.nq", 1, Sha256Hex(tboxBytes)),
                new ReleaseFileManifest("vocabulary", "vocabulary.nq", 1, Sha256Hex(vocabBytes)),
                new ReleaseFileManifest("abox", "abox.nq", 1, Sha256Hex(aboxBytes)),
            },
            ProvenanceCount: 0);
        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return dir;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task RewriteAsync_rewrites_nq_shards_ks_header_and_refreshes_manifest_hash()
    {
        SeedRelease("rel-1", "http://ontopilot.local/ks/1", "http://ontopilot.local/ks/1/onto#");

        var rewriter = new IriShardRewriter(NullLogger<IriShardRewriter>.Instance);
        var report = await rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/"),
            CancellationToken.None);

        // Every shard touched (each line carried a legacy prefix).
        var tboxLinesChanged = report.Steps
            .Where(s => s.Path.EndsWith("tbox.nq", StringComparison.Ordinal))
            .Sum(s => s.LinesChanged);
        Assert.True(tboxLinesChanged > 0);

        // 1) tbox.nq: prefix swapped in the IRI position.
        var tboxContent = File.ReadAllText(Path.Combine(_releasesRoot, "rel-1", "tbox.nq"));
        Assert.Contains("http://goodcrew.local/ks/1/onto#Animal", tboxContent);
        Assert.DoesNotContain("http://ontopilot.local/", tboxContent);

        // 2) abox.nq: subject + object + named-graph IRI all swapped.
        var aboxContent = File.ReadAllText(Path.Combine(_releasesRoot, "rel-1", "abox.nq"));
        Assert.Contains("http://goodcrew.local/ks/1/onto#rex", aboxContent);
        Assert.Contains("<http://goodcrew.local/ks/1/abox>", aboxContent);

        // 3) ks.json: GraphIri + BaseIri swapped.
        var ksJson = File.ReadAllText(Path.Combine(_releasesRoot, "rel-1", "ks.json"));
        Assert.Contains("http://goodcrew.local/ks/1", ksJson);
        Assert.DoesNotContain("http://ontopilot.local/", ksJson);

        // 4) manifest.json: SHA-256 entries refreshed; original SHA
        //    values must NOT match any more.
        var manifestJson = File.ReadAllText(Path.Combine(_releasesRoot, "rel-1", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson)!;
        var rewrittenTboxBytes = System.Text.Encoding.UTF8.GetBytes(tboxContent);
        Assert.Equal(Sha256Hex(rewrittenTboxBytes), manifest.Files.Single(f => f.Layer == "tbox").Sha256);
    }

    [Fact]
    public async Task RewriteAsync_dry_run_does_not_modify_files()
    {
        var dir = SeedRelease("rel-dry", "http://ontopilot.local/ks/2", "http://ontopilot.local/ks/2/onto#");
        var tboxBefore = File.ReadAllBytes(Path.Combine(dir, "tbox.nq"));
        var ksBefore = File.ReadAllText(Path.Combine(dir, "ks.json"));
        var manifestBefore = File.ReadAllText(Path.Combine(dir, "manifest.json"));

        var rewriter = new IriShardRewriter(NullLogger<IriShardRewriter>.Instance);
        await rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                FromPrefix: "http://ontopilot.local/",
                ToPrefix: "http://goodcrew.local/",
                DryRun: true),
            CancellationToken.None);

        Assert.Equal(tboxBefore, File.ReadAllBytes(Path.Combine(dir, "tbox.nq")));
        Assert.Equal(ksBefore, File.ReadAllText(Path.Combine(dir, "ks.json")));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(dir, "manifest.json")));
    }

    [Fact]
    public async Task RewriteAsync_is_idempotent_on_second_run()
    {
        SeedRelease("rel-idem", "http://ontopilot.local/ks/3", "http://ontopilot.local/ks/3/onto#");

        var rewriter = new IriShardRewriter(NullLogger<IriShardRewriter>.Instance);
        await rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);
        var afterFirst = File.ReadAllText(Path.Combine(_releasesRoot, "rel-idem", "tbox.nq"));

        // Second run: every line that contained the legacy prefix
        // has already been rewritten, so the file is untouched.
        await rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);
        var afterSecond = File.ReadAllText(Path.Combine(_releasesRoot, "rel-idem", "tbox.nq"));

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task RewriteAsync_rewrites_export_shards()
    {
        // Build a single export bundle:
        //   exports/pub-1/1/tbox-0000.nq
        //   exports/pub-1/1/vocabulary-0000.nq
        //   exports/pub-1/1/abox-0000.nq
        var dir = Path.Combine(_exportsRoot, "pub-1", "1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tbox-0000.nq"),
            "<http://ontopilot.local/ks/4/onto#Dog> <http://www.w3.org/2000/01/rdf-schema#label> \"Dog\" <http://ontopilot.local/ks/4> .\n");
        File.WriteAllText(Path.Combine(dir, "vocabulary-0000.nq"),
            "<http://ontopilot.local/vocab#defaultLanguage> <http://www.w3.org/2000/01/rdf-schema#label> \"x\" <http://ontopilot.local/ks/4/vocabulary> .\n");
        File.WriteAllText(Path.Combine(dir, "abox-0000.nq"),
            "<http://ontopilot.local/ks/4/onto#rover> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://ontopilot.local/ks/4/onto#Dog> <http://ontopilot.local/ks/4/abox> .\n");

        var rewriter = new IriShardRewriter(NullLogger<IriShardRewriter>.Instance);
        await rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                "http://ontopilot.local/", "http://goodcrew.local/"),
            CancellationToken.None);

        var tbox = File.ReadAllText(Path.Combine(dir, "tbox-0000.nq"));
        Assert.Contains("http://goodcrew.local/ks/4/onto#Dog", tbox);
        Assert.DoesNotContain("http://ontopilot.local/", tbox);
    }

    [Fact]
    public async Task RewriteAsync_throws_when_from_prefix_lacks_path_separator()
    {
        // Anchoring on / or # guards against substring collisions
        // (e.g. 'http://ontopilot.localized/' would otherwise match
        // the unanchored prefix 'http://ontopilot.local'). The
        // rewriter must reject these prefixes up front.
        var rewriter = new IriShardRewriter(NullLogger<IriShardRewriter>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => rewriter.RewriteAsync(
            new IriShardOptions(_releasesRoot, _exportsRoot,
                "http://ontopilot.local",   // no trailing / or #
                "http://goodcrew.local/"),
            CancellationToken.None));
    }
}
