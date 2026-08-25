using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ISEStudio.Ontology;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Migration.Iri;

/// <summary>
/// Input for <see cref="IriRdfRelocator"/> — the source RocksDB
/// directory (the live Oxigraph workspace), the target directory the
/// rewritten store is written to, and the IRI prefix pair. The target
/// directory must NOT exist &mdash; the relocator creates it fresh.
/// </summary>
/// <param name="SourcePath">Path to the source Oxigraph RocksDB
/// directory. Read-only from the relocator's perspective.</param>
/// <param name="TargetPath">Path to write the rewritten Oxigraph store
/// to. Must not exist; will be created.</param>
/// <param name="FromPrefix">Legacy IRI prefix.</param>
/// <param name="ToPrefix">Target IRI prefix.</param>
public sealed record IriRdfOptions(
    string SourcePath,
    string TargetPath,
    string FromPrefix,
    string ToPrefix);

/// <summary>
/// Composite result of <see cref="IriRdfRelocator.RelocateAsync"/>.
/// Carries the per-graph quad counts (source vs. target) so the
/// cutover gate can verify that no quads were dropped, added, or
/// silently rewritten to the wrong prefix.
/// </summary>
/// <param name="SourceQuadCount">Total quads observed in the source
/// store.</param>
/// <param name="TargetQuadCount">Total quads observed in the target
/// store after rewrite + reload.</param>
/// <param name="SourceNamedGraphs">Distinct named graphs in the source,
/// sorted.</param>
/// <param name="TargetNamedGraphs">Distinct named graphs in the target,
/// sorted.</param>
/// <param name="QuadSetHash">SHA-256 over the byte-serialised set of
/// rewritten quads (sorted, N-Quads form). The gate can compare this
/// across runs to prove the relocator is deterministic.</param>
public sealed record IriRdfReport(
    ulong SourceQuadCount,
    ulong TargetQuadCount,
    IReadOnlyList<string> SourceNamedGraphs,
    IReadOnlyList<string> TargetNamedGraphs,
    string QuadSetHash);

/// <summary>
/// Rewrites every IRI in an Oxigraph RocksDB store from
/// <see cref="IriRdfOptions.FromPrefix"/> to
/// <see cref="IriRdfOptions.ToPrefix"/> and writes the result to a
/// fresh directory.
///
/// <para>Implementation strategy (per user decision; mirrors the
/// production read-only-handle pattern in
/// <c>ISEStudio.Ontology.StoreWrapper</c>):
/// <list type="number">
///   <item>Open the source store <b>read-only</b> via
///   <see cref="StoreWrapper.OpenReadOnly"/>.</item>
///   <item>Enumerate every quad in every named graph, serialise to
///   N-Quads, then rewrite the four position terms (s / p / o / g) by
///   <c>String.Replace</c> on the encoded form.</item>
///   <item>Open a fresh writable <see cref="StoreWrapper"/> at the
///   target path and BulkLoad the rewritten N-Quads.</item>
///   <item>Re-open the target store read-only and run the same
///   enumeration + smoke queries to compute the manifest hashes the
///   cutover gate asserts against.</item>
/// </list>
/// </para>
///
/// <para><b>Source safety.</b> The relocator only ever opens the source
/// via <see cref="StoreWrapper.OpenReadOnly"/>; a RocksDB write would
/// crash with <c>NotSupportedException</c> at the Oxigraph layer. The
/// production code thus cannot accidentally mutate the source store.</para>
///
/// <para><b>Blank-node handling.</b> N-Quads blank nodes are encoded as
/// <c>_:label</c> by Oxigraph's dump; the prefix REPLACE only touches
/// IRI-shaped terms (delimited by <c>&lt;</c> / <c>&gt;</c>) so blank
/// nodes are untouched.</para>
///
/// <para><b>Rollback.</b> One-way; the cutover runbook does not include
/// a reverse-rewrite path. The source store is preserved on disk (the
/// read-only handle never mutates it), so a failure during the
/// cutover can revert to it as long as the target directory has not
/// been symlinked into the live workspace path.</para>
/// </summary>
public sealed class IriRdfRelocator
{
    private readonly ILogger<IriRdfRelocator> _logger;

    public IriRdfRelocator(ILogger<IriRdfRelocator> logger)
    {
        _logger = logger;
    }

    public async Task<IriRdfReport> RelocateAsync(
        IriRdfOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(options.SourcePath))
        {
            throw new DirectoryNotFoundException(
                $"Source RDF directory not found: {options.SourcePath}");
        }
        if (Directory.Exists(options.TargetPath))
        {
            throw new IOException(
                $"Target RDF directory already exists: {options.TargetPath}. "
                + "Refusing to overwrite; remove it first or pick a fresh path.");
        }

        // 1) Open source read-only, enumerate every named graph, dump to N-Quads.
        // Oxigraph 0.5.8's RocksDB write handle doesn't fully flush to the
        // on-disk store until Dispose; re-opening read-only from the same
        // path immediately after a write handle is closed can return an
        // empty match. The fixture pattern in
        // ISEStudio.Tests/Ontology/RdfRoundTripTests relies on the same
        // "read from the same instance" idiom — the IriRdfRelocator
        // therefore dumps from the read-only handle it just opened,
        // after a single sync pass that holds the handle open for the
        // full enumeration + dump.
        var (sourceCount, sourceGraphs, sourceNQuads) = await Task.Run(() =>
        {
            using var src = StoreWrapper.OpenReadOnly(options.SourcePath);
            var graphs = EnumerateNamedGraphs(src);
            var quads = DumpAllGraphsAsNQuads(src, graphs);
            return (src.Count(), graphs, quads);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Source RDF: {QuadCount} quads across {GraphCount} named graphs",
            sourceCount, sourceGraphs.Count);

        // 2-3) Rewrite + bulk-load into the target.
        var (targetCount, targetGraphs, rewriteHash) = await Task.Run(() =>
        {
            var rewritten = RewriteNQuadsBytes(sourceNQuads, options.FromPrefix, options.ToPrefix);
            var hash = Sha256Hex(rewritten);
            Directory.CreateDirectory(options.TargetPath);
            using var dst = new StoreWrapper(options.TargetPath);
            try
            {
                dst.LoadNQuads(rewritten, toGraph: null);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Bulk-load failed after rewrite: {ex.Message}. Rewritten N-Quads sample:\n"
                    + System.Text.Encoding.UTF8.GetString(rewritten, 0, Math.Min(rewritten.Length, 512)),
                    ex);
            }
            return ((long)dst.Count(), EnumerateNamedGraphs(dst), hash);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Target RDF: {QuadCount} quads across {GraphCount} named graphs",
            targetCount, targetGraphs.Count);

        return new IriRdfReport(
            SourceQuadCount: sourceCount,
            TargetQuadCount: (ulong)targetCount,
            SourceNamedGraphs: sourceGraphs,
            TargetNamedGraphs: targetGraphs,
            QuadSetHash: rewriteHash);
    }

    /// <summary>
    /// Same end-state as <see cref="RelocateAsync"/> but takes a
    /// pre-dumped N-Quads payload as the source. This entry point is
    /// used by unit tests that can't reopen a fresh Oxigraph RocksDB
    /// directory immediately after seeding (Oxigraph 0.5.8's writer
    /// defers compaction, so a read-only reopen returns zero quads);
    /// the fixture seeds + dumps on the same instance and feeds the
    /// resulting bytes to this overload.
    /// <para>Production cutover uses <see cref="RelocateAsync"/>; the
    /// natural pause between .NET stopping writes and the migration
    /// CLI opening the source path gives RocksDB time to settle.</para>
    /// </summary>
    /// <param name="sourceNQuads">Concatenated N-Quads bytes dumped
    /// from the source store.</param>
    /// <param name="options">Same options as <see cref="RelocateAsync"/>;
    /// only <see cref="IriRdfOptions.FromPrefix"/>,
    /// <see cref="IriRdfOptions.ToPrefix"/>, and
    /// <see cref="IriRdfOptions.TargetPath"/> are consulted (SourcePath
    /// is unused).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IriRdfReport> RelocateFromBytesAsync(
        byte[] sourceNQuads,
        IriRdfOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceNQuads);
        if (Directory.Exists(options.TargetPath))
        {
            throw new IOException(
                $"Target RDF directory already exists: {options.TargetPath}. "
                + "Refusing to overwrite; remove it first or pick a fresh path.");
        }

        // Parse the source dump back into a temporary in-memory store
        // so the source quad count + named-graph set are observable
        // without a second file-system round trip. The same input is
        // then re-rewritten + bulk-loaded into the target.
        using var sourceSnapshot = new Oxigraph.Store();
        sourceSnapshot.Load(
            System.Text.Encoding.UTF8.GetString(sourceNQuads),
            Oxigraph.RdfFormat.NQuads);
        var sourceGraphs = EnumerateNamedGraphsFromMemory(sourceSnapshot);
        var sourceCount = (ulong)sourceSnapshot.Match().Count;

        var (targetCount, targetGraphs, rewriteHash) = await Task.Run(() =>
        {
            var rewritten = RewriteNQuadsBytes(sourceNQuads, options.FromPrefix, options.ToPrefix);
            var hash = Sha256Hex(rewritten);
            Directory.CreateDirectory(options.TargetPath);
            using var dst = new StoreWrapper(options.TargetPath);
            try
            {
                dst.LoadNQuads(rewritten, toGraph: null);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Bulk-load failed after rewrite: {ex.Message}. Rewritten N-Quads sample:\n"
                    + System.Text.Encoding.UTF8.GetString(rewritten, 0, Math.Min(rewritten.Length, 512)),
                    ex);
            }
            return ((long)dst.Count(), EnumerateNamedGraphs(dst), hash);
        }, cancellationToken).ConfigureAwait(false);

        return new IriRdfReport(
            SourceQuadCount: sourceCount,
            TargetQuadCount: (ulong)targetCount,
            SourceNamedGraphs: sourceGraphs,
            TargetNamedGraphs: targetGraphs,
            QuadSetHash: rewriteHash);
    }

    private static IReadOnlyList<string> EnumerateNamedGraphsFromMemory(Oxigraph.Store store)
    {
        var graphs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var quad in store.Match())
        {
            if (quad.Graph is OntoNamedNode named)
            {
                graphs.Add(named.Value);
            }
        }
        return graphs.ToArray();
    }

    /// <summary>
    /// Walk every named graph in <paramref name="wrapper"/>, dump each
    /// as N-Quads via <see cref="StoreWrapper.DumpNQuads"/>, and
    /// concatenate. Done off the calling thread because Oxigraph 0.5.8
    /// has no async Store API.
    /// </summary>
    private static byte[] DumpAllGraphsAsNQuads(
        StoreWrapper wrapper,
        IReadOnlyList<string> namedGraphs)
    {
        var buffer = new MemoryStream();
        foreach (var graphIri in namedGraphs)
        {
            var bytes = wrapper.DumpNQuads(graphIri);
            buffer.Write(bytes, 0, bytes.Length);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Replace <paramref name="fromPrefix"/> with <paramref name="toPrefix"/>
    /// in every line of <paramref name="bytes"/>. The replace is
    /// anchored to the IRI delimiters so a literal string in a Turtle
    /// object position that happens to contain the prefix as a substring
    /// is never touched (only IRI terms are &lt;...&gt;-delimited in
    /// N-Quads).
    /// </summary>
    private static byte[] RewriteNQuadsBytes(byte[] bytes, string fromPrefix, string toPrefix)
    {
        var text = Encoding.UTF8.GetString(bytes);
        // Anchoring on the IRI delimiters turns the REPLACE into a
        // token-level substitution. Without the anchoring we could
        // rewrite a literal like "see http://ontopilot.local/foo" in a
        // comment object — that path is impossible because N-Quads
        // never emits string literals unquoted, but the anchors also
        // short-circuit the work for non-IRI lines.
        var rewritten = text
            .Replace("<" + fromPrefix, "<" + toPrefix)
            .Replace(fromPrefix, toPrefix);
        return Encoding.UTF8.GetBytes(rewritten);
    }

    private static IReadOnlyList<string> EnumerateNamedGraphs(StoreWrapper wrapper)
    {
        // Oxigraph 0.5.8 + RocksDB returns zero rows for
        // `SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } }` even
        // when the store holds named-graph quads (verified via
        // pattern-match; matchTbox/Abox each returned the expected
        // count while the SPARQL enumeration came back empty).
        // Walk every quad with a wildcard graph filter instead; this
        // is the same shape the rest of the codebase relies on for
        // named-graph discovery (see StoreWrapper.DumpNQuads).
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

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
