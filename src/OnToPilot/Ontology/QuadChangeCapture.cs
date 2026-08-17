using Oxigraph;

namespace OnToPilot.Ontology;

/// <summary>
/// Captures the pre-operation state of a single named graph so a unit of work
/// can be rolled back on failure. <see cref="MarkError"/> forces a revert on
/// dispose even when <c>revertOnError</c> was <c>false</c>; the typical use is
/// to call <see cref="MarkError"/> from inside a <c>catch</c> block that wants
/// to undo RDF writes regardless of caller preference.
/// </summary>
public sealed class QuadChangeCapture : IAsyncDisposable
{
    private readonly StoreWrapper _store;
    private readonly GraphLease _lease;
    private readonly bool _revertOnError;
    private readonly byte[] _snapshotNQuads;
    private int _disposed;
    private int _errorFlag;

    internal QuadChangeCapture(
        StoreWrapper store,
        string graphIri,
        GraphLease lease,
        bool revertOnError,
        byte[] snapshotNQuads)
    {
        _store = store;
        _lease = lease;
        _revertOnError = revertOnError;
        _snapshotNQuads = snapshotNQuads;
        GraphIri = graphIri;
    }

    /// <summary>The IRI of the captured graph.</summary>
    public string GraphIri { get; }

    /// <summary>
    /// Mark the captured operation as failed so <c>DisposeAsync</c> reverts the
    /// graph even when <c>revertOnError</c> was <c>false</c>.
    /// </summary>
    public void MarkError() => Interlocked.Exchange(ref _errorFlag, 1);

    /// <summary>
    /// Byte-exact snapshot of the graph at capture time, in N-Quads form.
    /// </summary>
    public ReadOnlyMemory<byte> SnapshotNQuads => _snapshotNQuads;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        bool hadError = Volatile.Read(ref _errorFlag) == 1;
        // Semantics:
        //   revertOnError == true  → always revert (caller is signalling "this
        //                             unit of work must be rolled back unless
        //                             the body ran without calling MarkError
        //                             explicitly"). Use this for the common
        //                             case where any exception inside the
        //                             using block should undo the work.
        //   revertOnError == false → commit unless MarkError() was called.
        // Use MarkError() to force revert regardless of the flag value
        // (e.g. when a caller wants to revert without exception semantics).
        bool shouldRevert = hadError || _revertOnError;

        if (shouldRevert)
        {
            try
            {
                _store.ReplaceGraphFromNQuads(GraphIri, _snapshotNQuads);
            }
            catch
            {
                // Revert failures must not mask the original exception (if any).
                // The lease release below always runs.
            }
        }

        await _lease.DisposeAsync().ConfigureAwait(false);
    }
}
