using System.Text;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoDefaultGraph = Oxigraph.DefaultGraph;

namespace OnToPilot.Ontology;

/// <summary>
/// Thin, application-facing wrapper around <see cref="Oxigraph.Store"/>.
/// Encapsulates the 0.5.8 API behind a small set of operations the rest of
/// OnToPilot can rely on, and supplies reversible per-graph writes via
/// <see cref="QuadChangeCapture"/>.
/// </summary>
/// <remarks>
/// <para>The wrapper deliberately exposes only the operations the rest of the
/// RDF layer needs. SPARQL <c>Query</c> / <c>Update</c> live with their
/// callers (<c>ABoxManager</c>, <c>SkosManager</c>, etc.) — see task 3.</para>
/// <para>Capture/revert snapshots are N-Quads byte buffers so the diff
/// preserves blank-node labels, language tags, and explicit datatypes —
/// verified by the format-round-trip tests.</para>
/// </remarks>
public sealed class StoreWrapper : IDisposable
{
    private readonly Oxigraph.Store _store;
    private readonly GraphWriteCoordinator _coordinator = new();
    private bool _disposed;

    /// <summary>
    /// Open a disk-backed store at <paramref name="path"/>. <c>null</c> would
    /// open an in-memory store; the wrapper always requires a path so the
    /// lifecycle is explicit.
    /// </summary>
    public StoreWrapper(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _store = new Oxigraph.Store(path);
    }

    /// <summary>
    /// Open a read-only handle against an existing on-disk store. Writes
    /// throw <see cref="NotSupportedException"/>; use the writable
    /// <see cref="StoreWrapper(string)"/> ctor for the workspace store.
    /// </summary>
    public static StoreWrapper OpenReadOnly(string path)
    {
        var wrapper = new StoreWrapper(path, Oxigraph.Store.OpenReadOnly(path));
        return wrapper;
    }

    // Private ctor that accepts an already-opened store (used by OpenReadOnly).
    private StoreWrapper(string path, Oxigraph.Store store)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _store = store;
    }

    // ------------------------------------------------------------------
    // CRUD primitives
    // ------------------------------------------------------------------

    /// <summary>Add quads to their respective named graphs.</summary>
    public void AddQuads(OntoNamedNode graph, IReadOnlyList<OntoQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(quads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (quads.Count == 0) return;
        _store.Extend(quads);
    }

    /// <summary>Remove quads from the store. No-op for quads that aren't present.</summary>
    public void RemoveQuads(OntoNamedNode graph, IReadOnlyList<OntoQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(quads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var q in quads)
        {
            _store.Remove(q);
        }
    }

    /// <summary>
    /// Pattern match. <c>graph</c> is the named-graph filter; <c>null</c>
    /// matches all graphs (the Oxigraph wildcard — quads in every named
    /// graph, including the default graph). To filter on the default graph
    /// specifically, pass <c>DefaultGraph</c>. See
    /// <see cref="Count(Oxigraph.NamedNode?)"/> for the same null convention
    /// (null → store total).
    /// </summary>
    public IReadOnlyList<OntoQuad> Match(
        OntoNamedNode? subject = null,
        OntoNamedNode? predicate = null,
        OntoLiteral? @object = null,
        OntoNamedNode? graph = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _store.Match(
            subject: subject,
            predicate: predicate,
            @object: @object,
            graph: (IGraphName?)graph);
    }

    /// <summary>
    /// Convenience overload that accepts string IRIs for subject/predicate
    /// and a graph IRI. <c>graphIri == null</c> means "default graph".
    /// </summary>
    public IReadOnlyList<OntoQuad> Match(
        string? subjectIri = null,
        string? predicateIri = null,
        string? objectIri = null,
        string? graphIri = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IGraphName graph = graphIri is null ? new OntoDefaultGraph() : new OntoNamedNode(graphIri);
        return _store.Match(
            subject: subjectIri is null ? null : new OntoNamedNode(subjectIri),
            predicate: predicateIri is null ? null : new OntoNamedNode(predicateIri),
            @object: objectIri is null ? null : new OntoNamedNode(objectIri),
            graph: graph);
    }

    /// <summary>
    /// Internal helper: pattern match by an exact <see cref="INamedOrBlankNode"/>
    /// subject. Used by callers that need to fetch triples whose subject is
    /// a blank node (Turtle <c>[ ... ]</c> property shapes, RDF lists, etc.)
    /// since the public string-IRI overloads only match named nodes.
    /// </summary>
    internal IReadOnlyList<OntoQuad> MatchSubject(
        INamedOrBlankNode subject,
        NamedNode? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.Match(subject: subject, predicate: predicate, @object: null, graph: null);
    }

    /// <summary>Total quad count. With <paramref name="graph"/>, scoped to one graph.</summary>
    public ulong Count(OntoNamedNode? graph = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (graph is null)
        {
            return _store.Count;
        }
        return (ulong)_store.Match(graph: graph).Count;
    }

    /// <summary>
    /// Wipe every named graph in the store. Used by tests to keep cases
    /// independent; production code should use per-graph replace or capture
    /// instead.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _store.Clear();
    }

    /// <summary>Whether the store contains this exact quad.</summary>
    public bool ContainsQuad(OntoQuad quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.Contains(quad);
    }

    /// <summary>
    /// Serialize one named graph as N-Quads bytes. The bytes preserve
    /// blank-node labels, language tags, and explicit datatypes — verified by
    /// the round-trip tests. We build the serialization in-process rather than
    /// going through <c>Store.Dump(RdfFormat.NQuads, FromGraph:)</c> because
    /// Oxigraph's N-Quads dump with a FromGraph filter strips the graph context
    /// AND collapses typed literals to bare Turtle syntax (e.g. <c>42</c>
    /// instead of <c>"42"^^xsd:integer</c>).
    /// </summary>
    public byte[] DumpNQuads(OntoNamedNode graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var quads = _store.Match(null, null, null, graph);
        var sb = new StringBuilder(quads.Count * 64);
        foreach (var q in quads)
        {
            AppendNQuadsTerm(sb, q.Subject);
            sb.Append(' ');
            AppendNQuadsTerm(sb, q.Predicate);
            sb.Append(' ');
            AppendNQuadsTerm(sb, q.Object);
            sb.Append(' ');
            AppendNQuadsTerm(sb, q.Graph);
            sb.Append(" .\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Convenience overload that accepts the named graph as a string IRI.</summary>
    public byte[] DumpNQuads(string graphIri)
    {
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        return DumpNQuads(new OntoNamedNode(graphIri));
    }

    // Centralised term writer — see NQuadsTermWriter for the canonical
    // N-Quads encoding rules. Keeping this as a thin delegate (rather than
    // inlining the body) means dumps, conflict signatures, and exports
    // always agree on byte content for the same term.
    private static void AppendNQuadsTerm(StringBuilder sb, object term) =>
        NQuadsTermWriter.Append(sb, term);

    /// <summary>
    /// Replace every quad in <paramref name="graph"/> with the supplied set in
    /// one logical operation. Implemented as <c>ClearGraph</c> +
    /// <c>Extend</c>; Oxigraph 0.5.8 has no single-call primitive.
    /// </summary>
    public void ReplaceGraph(OntoNamedNode graph, IReadOnlyList<OntoQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(quads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _store.ClearGraph(graph);
        if (quads.Count > 0)
        {
            _store.Extend(quads);
        }
    }

    /// <summary>
    /// Load a graph-aware N-Quads serialization into the store. Each line is
    /// an N-Quads statement; the graph context at the end of the line
    /// determines which named graph the quad lands in. Existing data is
    /// left intact. <paramref name="toGraph"/> is ignored: the graph context
    /// embedded in the document always wins so round-trips stay byte-exact.
    /// </summary>
    public void LoadNQuads(byte[] nQuads, OntoNamedNode? toGraph = null)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _store.Load(Encoding.UTF8.GetString(nQuads), RdfFormat.NQuads);
    }

    /// <summary>
    /// Load a Turtle serialization into a single named graph
    /// <paramref name="toGraph"/>. Used by the SHACL shapes file
    /// (<c>Ontology/Shapes/tbox-shapes.ttl</c>) which is shipped as Turtle
    /// for readability; the loaded quads land in <paramref name="toGraph"/>.
    /// </summary>
    public void LoadTurtle(byte[] turtle, OntoNamedNode toGraph)
    {
        ArgumentNullException.ThrowIfNull(turtle);
        ArgumentNullException.ThrowIfNull(toGraph);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var opts = new LoadOptions(ToGraph: toGraph);
        _store.Load(Encoding.UTF8.GetString(turtle), RdfFormat.Turtle, opts);
    }

    // Internal: replace a named graph by N-Quads bytes. Used by QuadChangeCapture
    // revert paths so the snapshot format is byte-exact and the embedded graph
    // context re-attaches the quads to the right slot.
    internal void ReplaceGraphFromNQuads(string graphIri, byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var graph = new OntoNamedNode(graphIri);
        _store.ClearGraph(graph);
        _store.Load(Encoding.UTF8.GetString(nQuads), RdfFormat.NQuads);
    }

    // ------------------------------------------------------------------
    // Capture / locks
    // ------------------------------------------------------------------

    /// <summary>
    /// Acquire a reversible write lease for the named graph. The lease is
    /// released on dispose; on dispose-with-error the graph is reverted to
    /// its pre-lease byte-exact N-Quads snapshot.
    /// </summary>
    public async ValueTask<QuadChangeCapture> CaptureAsync(
        OntoNamedNode graph,
        bool revertOnError,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var lease = await _coordinator.AcquireAsync(
            graph.Value,
            waitTimeout ?? TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        // Snapshot AFTER taking the lock so we capture exactly what the
        // caller will see as the "before" state.
        var snapshot = DumpNQuads(graph);

        return new QuadChangeCapture(this, graph.Value, lease, revertOnError, snapshot);
    }

    /// <summary>Convenience overload that accepts a string IRI.</summary>
    public ValueTask<QuadChangeCapture> CaptureAsync(
        string graphIri,
        bool revertOnError,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        return CaptureAsync(new OntoNamedNode(graphIri), revertOnError, waitTimeout, cancellationToken);
    }

    /// <summary>Acquire a shared read lease that blocks writers for its lifetime.</summary>
    public ValueTask<GraphLease> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _coordinator.ReadLockAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _coordinator.Dispose();
    }
}
