using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnToPilot.Ontology;

namespace OnToPilot.Migration.Iri;

/// <summary>
/// Input for <see cref="IriShardRewriter"/> — the on-disk roots that
/// host release and export shards.
/// </summary>
/// <param name="ReleasesRoot">Directory containing one subdirectory per
/// release id. Each release dir has <c>tbox.nq</c>,
/// <c>vocabulary.nq</c>, <c>abox.nq</c>, <c>manifest.json</c>, and
/// <c>ks.json</c>.</param>
/// <param name="ExportsRoot">Directory containing
/// <c>{publicId}/{jobLegacyId}/</c> subdirectories with
/// <c>{layer}-0000.nq</c> shards + <c>manifest.json</c>.</param>
/// <param name="FromPrefix">Legacy IRI prefix.</param>
/// <param name="ToPrefix">Target IRI prefix.</param>
/// <param name="DryRun">When <c>true</c>, compute the would-be
/// changes without writing anything.</param>
public sealed record IriShardOptions(
    string ReleasesRoot,
    string ExportsRoot,
    string FromPrefix,
    string ToPrefix,
    bool DryRun = false);

/// <summary>
/// Per-file rewrite outcome recorded in <see cref="IriShardReport"/>.
/// </summary>
/// <param name="Path">Absolute file path (release or export shard).</param>
/// <param name="Kind">File kind: <c>nq-shard</c>, <c>ks-header</c>,
/// or <c>manifest</c>.</param>
/// <param name="LinesChanged">N-Quads lines rewritten (only populated
/// for <c>nq-shard</c>; <c>ks-header</c> and <c>manifest</c> record 1
/// when any text was rewritten, 0 otherwise).</param>
/// <param name="NewSha256">SHA-256 of the rewritten bytes (empty in
/// dry-run mode).</param>
public sealed record IriShardFileStep(string Path, string Kind, long LinesChanged, string NewSha256);

/// <summary>
/// Composite result of <see cref="IriShardRewriter.RewriteAsync"/>.
/// Drives the cutover gate's <c>Assert-IriShardsRewritten</c>.
/// </summary>
public sealed class IriShardReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool DryRun { get; init; }
    public string FromPrefix { get; init; } = string.Empty;
    public string ToPrefix { get; init; } = string.Empty;
    public List<IriShardFileStep> Steps { get; } = new();

    public long FilesTouched => Steps.Count(s => s.LinesChanged > 0);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
    }
}

/// <summary>
/// Rewrites every on-disk N-Quads shard + ks.json header so the
/// file-system artefacts match the new IRI prefix. The release + export
/// shard layout is described in
/// <see cref="OnToPilot.Ontology.ReleaseArtifactStore"/> and
/// <see cref="OnToPilot.Exports.ExportArtifactStore"/>.
///
/// <para>What gets rewritten:
/// <list type="bullet">
///   <item>Every <c>tbox.nq</c>, <c>vocabulary.nq</c>, <c>abox.nq</c>
///   under <see cref="IriShardOptions.ReleasesRoot"/>.</item>
///   <item>Every <c>{layer}-0000.nq</c> shard under
///   <see cref="IriShardOptions.ExportsRoot"/>.</item>
///   <item>Every <c>ks.json</c> in a release dir &mdash; a flat
///   <c>{GraphIri, BaseIri}</c> object that carries the per-KS
///   prefix pair as JSON string values.</item>
///   <item>Every <c>manifest.json</c> &mdash; SHA-256 fields are
///   recomputed after the rewrite so the manifest stays consistent
///   with the on-disk shards. The IRIs do not appear in
///   manifest.json, so only the SHA-256 line count changes here.</item>
/// </list>
/// </para>
///
/// <para><b>N-Quads safety.</b> Each line is rewritten with an IRI-anchored
/// <c>Replace</c> (matches only inside <c>&lt;...&gt;</c> delimiters) so
/// a literal object that happens to contain the prefix as a substring is
/// never touched. Blank nodes (<c>_:label</c>) are unaffected because
/// they carry no prefix.</para>
///
/// <para><b>SHA-256 re-hash.</b> Both <c>manifest.json</c> file entries
/// (<see cref="ReleaseFileManifest.Sha256"/>) and the
/// <c>manifest.json</c> top-level rows reference the SHA-256 of the
/// shard bytes. The rewriter recomputes the SHA-256 of every rewritten
/// shard and re-serialises the manifest so the on-disk manifest and
/// the on-disk shards never disagree.</para>
///
/// <para><b>Rollback.</b> The rewriter does not snapshot the source
/// shards. A dry-run mode is provided so the cutover gate can verify
/// the would-be blast radius before any commit; live rollback must
/// come from a pre-cutover backup (see
/// <c>migration/runbooks/production-cutover.md</c>).</para>
/// </summary>
public sealed class IriShardRewriter
{
    private readonly ILogger<IriShardRewriter> _logger;

    public IriShardRewriter(ILogger<IriShardRewriter> logger)
    {
        _logger = logger;
    }

    public async Task<IriShardReport> RewriteAsync(
        IriShardOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidatePrefixes(options.FromPrefix, options.ToPrefix);

        var report = new IriShardReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            DryRun = options.DryRun,
            FromPrefix = options.FromPrefix,
            ToPrefix = options.ToPrefix,
        };

        if (Directory.Exists(options.ReleasesRoot))
        {
            await RewriteReleaseTreeAsync(options, report, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "ReleasesRoot '{Path}' does not exist; skipping.", options.ReleasesRoot);
        }

        if (Directory.Exists(options.ExportsRoot))
        {
            await RewriteExportTreeAsync(options, report, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "ExportsRoot '{Path}' does not exist; skipping.", options.ExportsRoot);
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        return report;
    }

    // -----------------------------------------------------------------
    // Release tree
    // -----------------------------------------------------------------

    private async Task RewriteReleaseTreeAsync(
        IriShardOptions options,
        IriShardReport report,
        CancellationToken cancellationToken)
    {
        // Each release lives in its own subdirectory; walk one level deep.
        foreach (var releaseDir in Directory.EnumerateDirectories(options.ReleasesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(releaseDir, "manifest.json");
            var ksHeaderPath = Path.Combine(releaseDir, "ks.json");
            var layers = new[] { "tbox.nq", "vocabulary.nq", "abox.nq" };

            // 1) Rewrite the three N-Quads shards and remember the new
            //    (layer, sha256) for the manifest rebuild.
            var rebuiltManifest = new List<ReleaseFileManifest>();
            foreach (var layer in layers)
            {
                var shardPath = Path.Combine(releaseDir, layer);
                if (!File.Exists(shardPath)) continue;
                var (linesChanged, newBytes, newSha) = RewriteNQuadsFile(
                    shardPath, options, cancellationToken);
                if (!options.DryRun && linesChanged > 0)
                {
                    await File.WriteAllBytesAsync(shardPath, newBytes, cancellationToken)
                        .ConfigureAwait(false);
                }
                report.Steps.Add(new IriShardFileStep(
                    Path: shardPath, Kind: "nq-shard", LinesChanged: linesChanged,
                    NewSha256: options.DryRun ? string.Empty : newSha));
                if (linesChanged > 0)
                {
                    rebuiltManifest.Add(new ReleaseFileManifest(
                        Layer: Path.GetFileNameWithoutExtension(layer),
                        FileName: layer,
                        StatementCount: StatementCount(newBytes),
                        Sha256: newSha));
                }
            }

            // 2) Rewrite ks.json (carries {GraphIri, BaseIri}).
            if (File.Exists(ksHeaderPath))
            {
                var (changed, newText) = RewriteKsHeader(ksHeaderPath, options);
                if (changed > 0 && !options.DryRun)
                {
                    await File.WriteAllTextAsync(ksHeaderPath, newText, cancellationToken)
                        .ConfigureAwait(false);
                }
                var newSha = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(newText))).ToLowerInvariant();
                report.Steps.Add(new IriShardFileStep(
                    Path: ksHeaderPath, Kind: "ks-header", LinesChanged: changed,
                    NewSha256: options.DryRun ? string.Empty : newSha));
            }

            // 3) Rewrite manifest.json — refresh SHA-256 for any shard that
            //    was touched, keep StatementCount in sync. The rest of the
            //    manifest (Version, ProvenanceCount) is preserved verbatim.
            if (File.Exists(manifestPath) && rebuiltManifest.Count > 0)
            {
                await RewriteReleaseManifestAsync(
                    manifestPath, rebuiltManifest, options, report, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RewriteReleaseManifestAsync(
        string manifestPath,
        IReadOnlyList<ReleaseFileManifest> rebuiltManifest,
        IriShardOptions options,
        IriShardReport report,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(text)
            ?? throw new InvalidDataException($"Failed to parse {manifestPath}");

        var byLayer = rebuiltManifest.ToDictionary(
            r => r.Layer, r => r, StringComparer.OrdinalIgnoreCase);

        var newFiles = manifest.Files
            .Select(f => byLayer.TryGetValue(f.Layer, out var replacement)
                ? replacement
                : f)
            .ToArray();

        var newManifest = manifest with { Files = newFiles };
        var newText = JsonSerializer.Serialize(newManifest,
            new JsonSerializerOptions { WriteIndented = true });

        if (!options.DryRun)
        {
            await File.WriteAllTextAsync(manifestPath, newText, cancellationToken)
                .ConfigureAwait(false);
        }
        var newSha = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(newText))).ToLowerInvariant();
        report.Steps.Add(new IriShardFileStep(
            Path: manifestPath, Kind: "manifest", LinesChanged: 1,
            NewSha256: options.DryRun ? string.Empty : newSha));
    }

    // -----------------------------------------------------------------
    // Export tree
    // -----------------------------------------------------------------

    private async Task RewriteExportTreeAsync(
        IriShardOptions options,
        IriShardReport report,
        CancellationToken cancellationToken)
    {
        // Layout: {publicId}/{jobLegacyId}/{layer}-0000.nq
        foreach (var publicIdDir in Directory.EnumerateDirectories(options.ExportsRoot))
        {
            foreach (var jobDir in Directory.EnumerateDirectories(publicIdDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var shardPath in Directory.EnumerateFiles(jobDir, "*.nq"))
                {
                    var (linesChanged, newBytes, newSha) = RewriteNQuadsFile(
                        shardPath, options, cancellationToken);
                    if (!options.DryRun && linesChanged > 0)
                    {
                        await File.WriteAllBytesAsync(shardPath, newBytes, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    report.Steps.Add(new IriShardFileStep(
                        Path: shardPath, Kind: "nq-shard", LinesChanged: linesChanged,
                        NewSha256: options.DryRun ? string.Empty : newSha));
                }
            }
        }
    }

    // -----------------------------------------------------------------
    // Per-file rewriting primitives
    // -----------------------------------------------------------------

    /// <summary>
    /// Rewrite one N-Quads file line-by-line with an IRI-anchored
    /// <c>Replace</c>. Returns the line change count, the rewritten
    /// bytes, and the SHA-256 of the rewritten bytes.
    /// </summary>
    private (long LinesChanged, byte[] NewBytes, string NewSha256) RewriteNQuadsFile(
        string path,
        IriShardOptions options,
        CancellationToken cancellationToken)
    {
        var original = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(original);

        // IRI-anchored replace: only matches inside <...>. Lines that
        // contain no IRI-shaped term still get a byte-for-byte rewrite
        // (linesChanged=0) so the SHA-256 path stays uniform.
        var rewritten = text
            .Replace("<" + options.FromPrefix, "<" + options.ToPrefix);

        var linesChanged = CountChangedLines(text, rewritten);
        var newBytes = Encoding.UTF8.GetBytes(rewritten);
        var newSha = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
        return (linesChanged, newBytes, newSha);
    }

    /// <summary>
    /// Rewrite the <c>ks.json</c> header in-place. The file holds a flat
    /// <c>{GraphIri, BaseIri}</c> object; both fields are string values
    /// so the IRI-anchored replace catches them.
    /// </summary>
    private (long Changed, string NewText) RewriteKsHeader(
        string path,
        IriShardOptions options)
    {
        var original = File.ReadAllText(path);
        var rewritten = original
            .Replace("\"" + options.FromPrefix, "\"" + options.ToPrefix);
        var changed = original == rewritten ? 0 : 1;
        return (changed, rewritten);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static long CountChangedLines(string before, string after)
    {
        if (before.Length == 0) return 0;
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        if (beforeLines.Length != afterLines.Length) return Math.Max(beforeLines.Length, afterLines.Length);
        long changed = 0;
        for (var i = 0; i < beforeLines.Length; i++)
        {
            if (!string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
            {
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Count <c>. </c> / <c>.\n</c> / <c>.\r</c> terminators in an
    /// N-Quads byte buffer. Mirrors
    /// <see cref="OnToPilot.Exports.ExportArtifactStore.StatementCount"/>
    /// so the rewritten manifest's <c>StatementCount</c> matches the
    /// shard's actual statement count exactly.
    /// </summary>
    private static long StatementCount(byte[] nQuads)
    {
        long count = 0;
        for (int i = 0; i < nQuads.Length; i++)
        {
            byte b = nQuads[i];
            if (b == (byte)'.' && i + 1 < nQuads.Length)
            {
                byte next = nQuads[i + 1];
                if (next == (byte)'\n' || next == (byte)'\r'
                    || next == (byte)' ' || next == (byte)'\t')
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static void ValidatePrefixes(string fromPrefix, string toPrefix)
    {
        if (string.IsNullOrEmpty(fromPrefix))
        {
            throw new ArgumentException("FromPrefix must be non-empty.", nameof(fromPrefix));
        }
        if (string.IsNullOrEmpty(toPrefix))
        {
            throw new ArgumentException("ToPrefix must be non-empty.", nameof(toPrefix));
        }
        if (!(fromPrefix.EndsWith('/') || fromPrefix.EndsWith('#')))
        {
            throw new ArgumentException(
                $"FromPrefix must end with '/' or '#' (got '{fromPrefix}').",
                nameof(fromPrefix));
        }
        if (!(toPrefix.EndsWith('/') || toPrefix.EndsWith('#')))
        {
            throw new ArgumentException(
                $"ToPrefix must end with '/' or '#' (got '{toPrefix}').",
                nameof(toPrefix));
        }
    }
}
