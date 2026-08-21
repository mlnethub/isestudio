using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

public sealed class OntologyProvenanceService
{
    private readonly OnToPilotDbContext _db;
    private readonly KnowledgeSystemAccessService _access;

    public OntologyProvenanceService(OnToPilotDbContext db, KnowledgeSystemAccessService access)
    { _db = db; _access = access; }

    public async Task<IReadOnlyList<SourceOut>?> ListSourcesAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer) throw new InvalidOperationException("Viewer access required for provenance.");

        var rows = await _db.AxiomProvenances.AsNoTracking()
            .Where(p => p.KnowledgeSystemId == ksId).ToListAsync(ct).ConfigureAwait(false);

        var chunkIds = rows.Where(r => r.ChunkId is not null).Select(r => r.ChunkId!.Value).Distinct().ToList();
        var docByChunk = new Dictionary<Guid, Guid>();
        if (chunkIds.Count > 0)
            foreach (var c in await _db.Chunks.AsNoTracking().Where(c => chunkIds.Contains(c.Id)).ToListAsync(ct).ConfigureAwait(false))
                docByChunk[c.Id] = c.DocumentId;

        var docIds = docByChunk.Values.Distinct().ToList();
        var docs = docIds.Count > 0
            ? (await _db.Documents.AsNoTracking().Where(d => docIds.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id)
            : new Dictionary<Guid, DocumentEntity>();

        var axiomsByDoc = new Dictionary<Guid, HashSet<string>>();
        var chunksByDoc = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var r in rows)
        {
            if (r.ChunkId is null) continue;
            if (!docByChunk.TryGetValue(r.ChunkId.Value, out var docId)) continue;
            if (!axiomsByDoc.TryGetValue(docId, out var ax)) { ax = new HashSet<string>(); axiomsByDoc[docId] = ax; }
            ax.Add(r.AxiomKey);
            if (!chunksByDoc.TryGetValue(docId, out var ch)) { ch = new HashSet<Guid>(); chunksByDoc[docId] = ch; }
            ch.Add(r.ChunkId.Value);
        }

        return axiomsByDoc.Select(kv =>
        {
            docs.TryGetValue(kv.Key, out var d);
            return new SourceOut(
                DocumentId: kv.Key,
                Filename: d is not null ? d.OriginalFilename : "(deleted)",
                Folder: d is not null ? d.Folder : null,
                Exists: d is not null,
                ChunkCount: chunksByDoc.TryGetValue(kv.Key, out var s) ? s.Count : 0,
                AxiomCount: kv.Value.Count);
        }).OrderByDescending(x => x.AxiomCount).ToList();
    }

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> ResolveUserAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userGuid, ct).ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return (null, null);
        return (user, ks);
    }
}
