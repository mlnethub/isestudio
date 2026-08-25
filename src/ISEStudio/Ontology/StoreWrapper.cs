using System.Diagnostics;
using System.Text;
using Oxigraph;
using ISEStudio.Observability;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoDefaultGraph = Oxigraph.DefaultGraph;

namespace ISEStudio.Ontology;

/// <summary>
/// Thin, application-facing wrapper around <see cref="Oxigraph.Store"/>.
/// Encapsulates the 0.5.8 API behind a small set of operations the rest of
/// ISEStudio can rely on, and supplies reversible per-graph writes via
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
        using var activity = Telemetry.RdfSource.StartActivity("rdf.store.add", ActivityKind.Internal);
        activity?.SetTag(TelemetryExtensions.PeerServiceTag, "oxigraph");
        activity?.SetTag(TelemetryExtensions.GraphTag, graph.Value);
        activity?.SetTag(TelemetryExtensions.QuadCountTag, quads.Count);
        try
        {
            _store.Extend(quads);
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "success");
        }
        catch (Exception ex)
        {
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Remove quads from the store. No-op for quads that aren't present.</summary>
    public void RemoveQuads(OntoNamedNode graph, IReadOnlyList<OntoQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(quads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (quads.Count == 0) return;
        using var activity = Telemetry.RdfSource.StartActivity("rdf.store.remove", ActivityKind.Internal);
        activity?.SetTag(TelemetryExtensions.GraphTag, graph.Value);
        activity?.SetTag(TelemetryExtensions.QuadCountTag, quads.Count);
        try
        {
            foreach (var q in quads)
            {
                _store.Remove(q);
            }
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "success");
        }
        catch (Exception ex)
        {
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
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

    /// <summary>
    /// Execute a read-only SPARQL query against the workspace store and
    /// project the bindings into <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// rows. Supports SELECT (row-per-solution, keys = SPARQL variables)
    /// and ASK (single row, <c>{ "boolean": bool }</c>); CONSTRUCT / DESCRIBE
    /// are not reachable through <see cref="Api.ReadOnlySparqlPolicy"/>
    /// but the dispatcher still projects them as <c>{ s, p, o }</c> rows
    /// so the wire envelope stays stable.
    /// </summary>
    /// <param name="sparql">SPARQL text; already passed the read-only guard.</param>
    /// <param name="options">Per-call options; pass
    /// <c>QueryOptions.DefaultGraphs = [tbox, abox, vocab]</c> to KS-bind.</param>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(
        string sparql,
        QueryOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sparql);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var activity = Telemetry.RdfSource.StartActivity("rdf.store.query", ActivityKind.Internal);
        try
        {
            using var results = _store.Query(sparql, options);
            var rows = ProjectQueryResults(results);
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "success");
            return rows;
        }
        catch (Exception ex)
        {
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Async overload. Oxigraph 0.5.8 has no native async query API, so
    /// the call is dispatched to the thread pool to keep the controller
    /// scope non-blocking. Cancellation is observed before the work is
    /// scheduled.
    /// </summary>
    public ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sparql,
        QueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sparql);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            Task.Run(() => Query(sparql, options), cancellationToken));
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectQueryResults(
        QueryResults results)
    {
        switch (results)
        {
            case QuerySolutions solutions:
            {
                var rows = new List<IReadOnlyDictionary<string, object?>>(solutions.Count);
                foreach (var sol in solutions)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var v in solutions.Variables)
                    {
                        // Oxigraph exposes two indexing paths:
                        //   - QuerySolution[Variable] -> ITerm
                        //   - QuerySolution.TryGetValue(string, out ITerm)
                        // The string-keyed indexer is what we want so the
                        // wire row matches the SPARQL variable name verbatim.
                        if (sol.TryGetValue(v.Value, out var term))
                        {
                            dict[v.Value] = ProjectTerm(term);
                        }
                        else
                        {
                            dict[v.Value] = null;
                        }
                    }
                    rows.Add(dict);
                }
                return rows;
            }
            case QueryBoolean ask:
            {
                // Single-row projection so SELECT-style row iteration
                // works uniformly on the caller side.
                return new IReadOnlyDictionary<string, object?>[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["boolean"] = ask.Value,
                    },
                };
            }
            case QueryTriples triples:
            {
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var t in triples)
                {
                    rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["s"] = ProjectTerm(t.Subject),
                        ["p"] = t.Predicate.Value,
                        ["o"] = ProjectTerm(t.Object),
                    });
                }
                return rows;
            }
            default:
                return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }
    }

    private static object? ProjectTerm(ITerm? term)
    {
        if (term is null) return null;
        switch (term)
        {
            case OntoNamedNode node:
                return node.Value;
            case OntoBlankNode blank:
                // Oxigraph 0.5.8 stores the bare label; prefix with "_:"
                // to align with N-Quads wire encoding the rest of the
                // store emits via DumpNQuads.
                return "_:" + blank.Value;
            case OntoLiteral literal:
                // Datatype is nullable at runtime — plain literals (e.g.
                // rdfs:label "Rex" with no explicit ^^xsd:string) carry a
                // null Datatype. Both plain and typed literals project as
                // the lexical value; the .NET SPARQL wire is simpler than
                // Python's {type,value,datatype} binding (a known fidelity
                // gap, accepted in slice 5). The null-check below avoids a
                // NullReferenceException that previously surfaced as HTTP
                // 500 whenever a SELECT returned a string literal.
                if (literal.Datatype is not null
                    && literal.Datatype.Value != "http://www.w3.org/2001/XMLSchema#string")
                {
                    return literal.Value;
                }
                return literal.Value;
            default:
                return term.ToString();
        }
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

    /// <summary>
    /// Compute the symmetric set difference between two N-Quads
    /// serialisations of the same graph. Returns the added and removed
    /// N-Quads blobs in the same dump format <see cref="DumpNQuads"/>
    /// produces, so they round-trip through <see cref="LoadNQuads"/> on
    /// rollback. Lines are deduplicated by their full text (after
    /// trimming the trailing newline) so a triple added twice still
    /// counts as zero net additions.
    /// </summary>
    public static (byte[] Added, byte[] Removed) DiffNQuads(byte[] pre, byte[] post)
    {
        ArgumentNullException.ThrowIfNull(pre);
        ArgumentNullException.ThrowIfNull(post);

        var preSet = SplitLines(pre);
        var postSet = SplitLines(post);

        var added = new SortedSet<string>(postSet, StringComparer.Ordinal);
        var removed = new SortedSet<string>(preSet, StringComparer.Ordinal);
        added.ExceptWith(preSet);
        removed.ExceptWith(postSet);

        return (JoinLines(added), JoinLines(removed));
    }

    /// <summary>
    /// Parse raw UTF-8 N-Quads bytes into a list of <see cref="OntoQuad"/>.
    /// Each N-Quads line carries its own graph IRI (4th term), so callers
    /// can feed the result directly to <see cref="AddQuads"/>/<see cref="RemoveQuads"/>
    /// — the graph arg there is telemetry-only; each quad routes by its own Graph.
    /// Used by history rollback to replay inverse audit blobs (the audit
    /// <c>Added</c>/<c>Removed</c> columns are raw N-Quads, not gzipped).
    /// </summary>
    public static IReadOnlyList<OntoQuad> ParseNQuads(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        using var tmp = new Oxigraph.Store();
        tmp.Load(Encoding.UTF8.GetString(nQuads), RdfFormat.NQuads);
        return tmp.Match().ToList();
    }

    private static HashSet<string> SplitLines(byte[] bytes)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (bytes.Length == 0) return result;
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r', ' ');
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    private static byte[] JoinLines(SortedSet<string> lines)
    {
        if (lines.Count == 0) return Array.Empty<byte>();
        var sb = new StringBuilder(lines.Count * 64);
        foreach (var line in lines)
        {
            sb.Append(line);
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
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

        using var activity = Telemetry.RdfSource.StartActivity("rdf.store.replace", ActivityKind.Internal);
        activity?.SetTag(TelemetryExtensions.GraphTag, graph.Value);
        activity?.SetTag(TelemetryExtensions.QuadCountTag, quads.Count);
        try
        {
            _store.ClearGraph(graph);
            if (quads.Count > 0)
            {
                _store.Extend(quads);
            }
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "success");
        }
        catch (Exception ex)
        {
            activity?.SetTag(TelemetryExtensions.OutcomeTag, "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
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

    /// <summary>
    /// Load a Turtle serialization with a one-shot prefix rewrite so a
    /// static <c>.ttl</c> file declaring <c>@prefix op:</c> against the
    /// legacy IRI can be loaded into a host configured for a different
    /// vocabulary namespace. Replaces every occurrence of
    /// <paramref name="fromPrefix"/> in the Turtle text with
    /// <paramref name="toPrefix"/> before parsing.
    /// </summary>
    /// <remarks>
    /// Used by <c>tbox-shapes.ttl</c>: the file ships with
    /// <c>@prefix op: &lt;http://goodcrew.local/vocab#&gt;</c> baked in
    /// (it's a Content item, not a parameterised template). At load time
    /// we substitute the configured <c>ISEStudioOptions.VocabNamespace</c>
    /// so the shape subject IRI stays aligned with
    /// <see cref="SkosVocab.IseStudio"/>.
    /// </remarks>
    public void LoadTurtleWithPrefixRewrite(
        byte[] turtle, OntoNamedNode toGraph, string fromPrefix, string toPrefix)
    {
        ArgumentNullException.ThrowIfNull(turtle);
        ArgumentNullException.ThrowIfNull(toGraph);
        ArgumentException.ThrowIfNullOrEmpty(fromPrefix);
        ArgumentException.ThrowIfNullOrEmpty(toPrefix);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var raw = Encoding.UTF8.GetString(turtle);
        var rewritten = fromPrefix == toPrefix ? raw : raw.Replace(fromPrefix, toPrefix);
        var opts = new LoadOptions(ToGraph: toGraph);
        _store.Load(rewritten, RdfFormat.Turtle, opts);
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

        return await Telemetry.RdfSource.WithRdfActivity(
            "rdf.store.capture",
            graph.Value,
            async ct =>
            {
                var lease = await _coordinator.AcquireAsync(
                    graph.Value,
                    waitTimeout ?? TimeSpan.FromSeconds(15),
                    ct).ConfigureAwait(false);

                // Snapshot AFTER taking the lock so we capture exactly what the
                // caller will see as the "before" state.
                var snapshot = DumpNQuads(graph);

                return new QuadChangeCapture(this, graph.Value, lease, revertOnError, snapshot);
            },
            cancellationToken).ConfigureAwait(false);
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
