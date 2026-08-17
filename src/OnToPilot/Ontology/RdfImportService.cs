using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;

namespace OnToPilot.Ontology;

/// <summary>Import mode for <see cref="RdfImportService.ImportAsync"/>.</summary>
public enum ImportMode
{
    /// <summary>Append quads to the layer.</summary>
    Merge,

    /// <summary>Replace the layer contents with the input (clear + merge).</summary>
    Replace,
}

/// <summary>
/// Layered importer for N-Quads payloads. Each layer's import runs inside a
/// <see cref="StoreWrapper.CaptureAsync"/> window so the work commits on
/// success and reverts on failure.
///
/// <para><see cref="StoreWrapper.CaptureAsync"/>'s <c>revertOnError</c>
/// semantics are inverted from typical "rollback on throw" — <c>true</c>
/// means "always revert", <c>false</c> means "commit unless MarkError
/// fires". We pass <c>false</c> so success commits, then call
/// <see cref="QuadChangeCapture.MarkError"/> from a <c>catch</c> block to
/// force the revert on any exception. The clear step of
/// <see cref="ImportMode.Replace"/> runs inside the capture so a merge
/// failure reverts the clear too.</para>
/// </summary>
public sealed class RdfImportService
{
    private readonly StoreWrapper _store;

    public RdfImportService(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Import an N-Quads payload into one of the three workspace layers.
    /// Blank nodes, language tags, and datatypes survive the round trip —
    /// the loader uses Oxigraph's N-Quads parser and reattaches the graph
    /// context from each statement. For <see cref="ImportMode.Replace"/>
    /// the layer is cleared inside the capture so the revert path restores
    /// the pre-import state if the merge throws.
    /// </summary>
    public async Task ImportAsync(
        KsContext ks,
        RdfLayer layer,
        byte[] nQuads,
        ImportMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(nQuads);

        var graphIri = LayerGraph(ks, layer);
        await using var capture = await _store.CaptureAsync(
            graphIri, revertOnError: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var graph = new OntoNamedNode(graphIri);

            if (mode == ImportMode.Replace)
            {
                _store.ReplaceGraph(graph, Array.Empty<OntoQuad>());
            }

            _store.LoadNQuads(nQuads, graph);
        }
        catch
        {
            capture.MarkError();
            throw;
        }
    }

    private static string LayerGraph(KsContext ks, RdfLayer layer) => ReleaseManager.GraphIriFor(ks, layer);
}