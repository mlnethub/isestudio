using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Ontology;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Ontology;

/// <summary>
/// Knowledge-system ontology mutation surface. Mirrors the Python
/// <c>backend/app/api/ontology.py</c> edit + reset endpoints. Each
/// operation is gated by the calling user's effective role on the KS
/// (Editor for edits, Owner for reset), and writes an
/// <see cref="AuditEventEntity"/> row with the byte-exact N-Quads diff
/// the change produced so future rollback paths can replay the
/// negation. The diff is captured by snapshotting the TBox graph before
/// and after the editor call &mdash; the editor's own
/// <see cref="QuadChangeCapture"/> handles the Oxigraph lock and
/// revert-on-error semantics, so the service stays above the capture
/// abstraction.
/// </summary>
public sealed class OntologyService
{
    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly OntologyEditor _editor;
    private readonly StoreWrapper _store;
    private readonly OntologyViewBuilder _builder;
    private readonly KnowledgeStatsService _stats;

    public OntologyService(
        ISEStudioDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        OntologyEditor editor,
        StoreWrapper store,
        OntologyViewBuilder builder,
        KnowledgeStatsService stats)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _editor = editor;
        _store = store;
        _builder = builder;
        _stats = stats;
    }

    // ----------------------------------------------------------------------
    // Edit
    // ----------------------------------------------------------------------

    /// <summary>
    /// Apply a single structured edit against the KS's TBox graph. Returns
    /// the affected <see cref="OntologyEditResult"/> on success; returns
    /// <c>null</c> when the KS is not visible to the caller (the dispatcher
    /// maps that to a 404). Throws <see cref="InvalidOperationException"/>
    /// for insufficient role, unknown <c>op</c>, or invalid payload &mdash;
    /// the global middleware translates that to the FastAPI
    /// <c>{"detail": "..."}</c> envelope.
    /// </summary>
    public async Task<OntologyEditResult?> EditAsync(
        Guid ksId,
        IReadOnlyDictionary<string, object?> op,
        Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(op);
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
        {
            throw new InvalidOperationException(
                "Editor access is required to edit ontology.");
        }

        var opName = op.TryGetValue("op", out var opObj) && opObj is string s ? s : "edit";
        var pre = _store.DumpNQuads(ks.GraphIri);
        string result;
        try
        {
            result = await _editor.ApplyEditAsync(ks.GraphIri, ks.BaseIri, op, ct).ConfigureAwait(false);
        }
        catch (OntologyEditException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        var post = _store.DumpNQuads(ks.GraphIri);
        var (added, removed) = StoreWrapper.DiffNQuads(pre, post);

        await WriteAuditAsync(ks.Id, user, "ontology.edit",
            $"Ontology edit ({opName})",
            op, ks.GraphIri, added, removed, ct).ConfigureAwait(false);

        // Refresh the cached ClassCount/PropertyCount/AxiomCount columns
        // so the home-page list cards stay in sync with the live graph
        // (Python's ontology.py:138 calls refresh_ks_stats here). The
        // counts are best-effort: a failure must not roll back the edit
        // (it already committed to the audit log), so swallow exceptions
        // and log via the catch below if needed in the future.
        await _stats.RefreshAsync(ks.Id, ct).ConfigureAwait(false);

        return new OntologyEditResult(result);
    }

    // ----------------------------------------------------------------------
    // Reset
    // ----------------------------------------------------------------------

    /// <summary>
    /// Drop every quad in the KS TBox graph AND the paired ABox graph.
    /// Owners only. Mirrors the Python
    /// <c>backend/app/api/ontology.py::reset_ontology</c> flow that
    /// calls both <c>clear_graph</c> paths in one transaction.
    /// </summary>
    public async Task<OntologyEditResult?> ResetAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Owner)
        {
            throw new InvalidOperationException(
                "Owner access is required to reset ontology.");
        }

        var aboxGraphIri = AboxIri(ks.GraphIri);
        var preTBox = _store.DumpNQuads(ks.GraphIri);
        var preABox = _store.DumpNQuads(aboxGraphIri);

        var tboxGraph = new OntoNamedNode(ks.GraphIri);
        var aboxGraph = new OntoNamedNode(aboxGraphIri);
        var tboxQuads = _store.Match(graph: tboxGraph);
        if (tboxQuads.Count > 0)
        {
            _store.RemoveQuads(tboxGraph, tboxQuads);
        }
        var aboxQuads = _store.Match(graph: aboxGraph);
        if (aboxQuads.Count > 0)
        {
            _store.RemoveQuads(aboxGraph, aboxQuads);
        }

        var postTBox = _store.DumpNQuads(ks.GraphIri);
        var postABox = _store.DumpNQuads(aboxGraphIri);
        var (addedTBox, removedTBox) = StoreWrapper.DiffNQuads(preTBox, postTBox);
        var (addedABox, removedABox) = StoreWrapper.DiffNQuads(preABox, postABox);

        var added = Concat(addedTBox, addedABox);
        var removed = Concat(removedTBox, removedABox);

        await WriteAuditAsync(ks.Id, user, "ontology.reset",
            "Reset ontology (cleared TBox and ABox graphs)",
            detail: null, ks.GraphIri, added, removed, ct).ConfigureAwait(false);

        // After wiping both graphs the TBox stats drop back to zero;
        // refresh so the home page doesn't show stale counts from before
        // the reset (mirrors Python's ontology.py:188).
        await _stats.RefreshAsync(ks.Id, ct).ConfigureAwait(false);

        return new OntologyEditResult(ks.GraphIri);
    }

    // ----------------------------------------------------------------------
    // View
    // ----------------------------------------------------------------------

    /// <summary>
    /// Read the curated TBox view for the given knowledge system. Returns
    /// <c>null</c> when the caller is not resolvable (no actor id) or when
    /// the KS row no longer exists (deleted between resolve + access).
    /// Throws <see cref="InvalidOperationException"/> when the caller's
    /// effective role is below <see cref="KSRole.Viewer"/>.
    /// </summary>
    public async Task<OntologyResponse?> GetViewAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer)
            throw new InvalidOperationException(
                "Viewer access is required to read the ontology view.");

        var view = await _builder.BuildFromStoreAsync(_store, ks.GraphIri, ct).ConfigureAwait(false);
        return view with
        {
            KnowledgeSystem = new KnowledgeSystemMeta(
                Id: ks.Id,
                Name: ks.Name,
                BaseIri: ks.BaseIri,
                Release: null),
        };
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> ResolveUserAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null) return (null, null);
        return (user, ks);
    }

    /// <summary>
    /// Append the audit row that records the change. The
    /// <paramref name="added"/> / <paramref name="removed"/> blobs are
    /// the byte-exact N-Quads diff (raw, not gzipped &mdash; the Python
    /// backend gzips for storage savings; the .NET port keeps the raw
    /// bytes for now to avoid adding a Zip dependency to a service that
    /// has no other compression needs).
    /// </summary>
    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, string summary,
        IReadOnlyDictionary<string, object?>? detail,
        string? graph,
        byte[] added, byte[] removed,
        CancellationToken token)
    {
        JsonDocument? detailDoc = null;
        if (detail is not null)
        {
            var json = JsonSerializer.Serialize(detail);
            detailDoc = JsonDocument.Parse(json);
        }
        _db.AuditEvents.Add(new AuditEventEntity
        {
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = summary,
            Detail = detailDoc,
            Graph = graph,
            Added = added.Length == 0 ? null : added,
            Removed = removed.Length == 0 ? null : removed,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>The instance graph paired with a TBox graph (mirrors Python <c>_abox_iri</c>).</summary>
    private static string AboxIri(string graphIri) =>
        graphIri.TrimEnd('/') + "/abox";

    /// <summary>
    /// Concatenate two N-Quads blobs by stripping the empty case and
    /// joining the newline-terminated lines. <c>DiffNQuads</c> always
    /// terminates with a newline, so the concatenation is safe.
    /// </summary>
    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        var combined = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, combined, 0, a.Length);
        Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
        return combined;
    }
}
