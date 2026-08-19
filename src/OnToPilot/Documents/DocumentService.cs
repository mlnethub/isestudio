using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Storage;

namespace OnToPilot.Documents;

/// <summary>
/// Document CRUD + parse + chunks + contribution + move + delete surface.
/// Aligned with <c>backend/app/api/documents.py</c> (510 LOC).
///
/// <para>The service is the dispatcher-routed documents admin seam;
/// controllers bind <c>GET / POST / PATCH / DELETE /api/knowledge/{ks_id}/documents*</c>
/// through <see cref="OnToPilot.Integration.InternalOperationDispatcher"/>
/// and the dispatcher hands a scoped instance of this service the
/// <see cref="OnToPilotDbContext"/> already opened for the request.
/// <c>documents.upload</c> is the single exception: that operation is
/// handled by the controller directly because the request body is
/// <c>multipart/form-data</c>, which doesn't fit the JSON envelope the
/// facade carries.</para>
///
/// <para>Role gates mirror the Python dependencies: list / get / chunks /
/// contribution / impact require <see cref="KSRole.Viewer"/>, upload /
/// parse / move / delete require <see cref="KSRole.Editor"/> on the KS
/// (or admin).</para>
/// </summary>
public sealed class DocumentService
{
    /// <summary>Default chunk size — matches Python <c>parser.CHUNK_SIZE</c>.</summary>
    public const int DefaultChunkSize = 1500;

    /// <summary>Default chunk overlap — matches Python <c>parser.CHUNK_OVERLAP</c>.</summary>
    public const int DefaultChunkOverlap = 200;

    /// <summary>
    /// Lowercase extensions (no dot) accepted at upload time. Mirrors
    /// <c>DocumentParser.Supported</c> and <c>TestDocumentParser.Supported</c>;
    /// keep in sync if the production parser set ever changes.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedUploadExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pdf", "docx", "doc", "xlsx", "xls", "txt", "md", "markdown", "csv",
        };

    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly IBlobStore _blobs;
    private readonly IDocumentParser _parser;
    private readonly Chunker _chunker;
    private readonly ILogger<DocumentService>? _logger;

    public DocumentService(
        OnToPilotDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        IBlobStore blobs,
        IDocumentParser parser,
        Chunker chunker,
        ILogger<DocumentService>? logger = null)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _blobs = blobs;
        _parser = parser;
        _chunker = chunker;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // List / get / upload / move
    // ----------------------------------------------------------------------

    /// <summary>List every document in the KS, newest first.</summary>
    public async Task<IReadOnlyList<DocumentOut>?> ListAsync(long ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        // SQLite refuses DateTimeOffset in ORDER BY; materialise then sort.
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt));
        return rows.Select(Project).ToList();
    }

    /// <summary>
    /// Paginated list with optional <c>folder</c> / <c>q</c> (filename
    /// substring) / <c>status</c> filters plus a <c>folders</c> distinct
    /// enumeration.
    /// </summary>
    public async Task<DocumentListResponse?> ListPageAsync(
        long ksId, string? folder, string? q, string? status,
        int limit, int offset, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;

        if (limit < 1 || limit > 100)
            throw new InvalidOperationException("limit must be between 1 and 100.");
        if (offset < 0)
            throw new InvalidOperationException("offset must be >= 0.");

        var conditions = new List<System.Linq.Expressions.Expression<Func<DocumentEntity, bool>>>(
            capacity: 4)
        {
            d => d.KnowledgeSystemId == ks.Id,
        };
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var normFolder = NormalizeFolder(folder);
            conditions.Add(d => d.Folder == normFolder);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            conditions.Add(d => EF.Functions.Like(d.OriginalFilename, like));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var st = status.Trim();
            conditions.Add(d => d.ParseStatus == st);
        }

        var baseQuery = conditions.Aggregate(
            (System.Linq.IQueryable<DocumentEntity>)_db.Documents.AsNoTracking(),
            (qAcc, c) => qAcc.Where(c));

        var total = await baseQuery.LongCountAsync(ct).ConfigureAwait(false);

        // Apply ordering client-side as well (DateTimeOffset dodge).
        var rows = await baseQuery.ToListAsync(ct).ConfigureAwait(false);
        rows.Sort((a, b) =>
        {
            var cmp = b.UploadedAt.CompareTo(a.UploadedAt);
            return cmp != 0 ? cmp : b.Id.CompareTo(a.Id);
        });
        var pageRows = rows.Skip(offset).Take(limit).ToList();

        var folders = await _db.Documents.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ks.Id)
            .Select(d => d.Folder)
            .Distinct()
            .OrderBy(f => f)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new DocumentListResponse(
            Items: pageRows.Select(Project).ToList(),
            Total: total,
            Folders: folders);
    }

    /// <summary>
    /// Stream an upload into the blob store, dedup at the (ks, sha256)
    /// level, insert a <see cref="DocumentEntity"/> row when no prior
    /// document in this KS already references the same bytes.
    /// </summary>
    public async Task<DocumentOut> UploadAsync(
        long ksId, Stream content, string fileName, string? mime,
        long sizeBytes, string folder, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) throw new InvalidOperationException("Knowledge system not found.");

        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (sizeBytes <= 0)
            throw new InvalidOperationException("Empty file");

        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
            throw new InvalidOperationException("Filename has no extension.");
        if (!SupportedUploadExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"Unsupported file type: .{ext}");

        var blob = await _blobs.PutAsync(content, ct).ConfigureAwait(false);

        // Per-KS content-addressed dedup: identical bytes already in
        // *this* KS → same row, just move it to the target folder.
        var existing = await _db.Documents
            .FirstOrDefaultAsync(d => d.KnowledgeSystemId == ks.Id && d.Sha256 == blob.Sha256, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Folder = NormalizeFolder(folder);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await WriteAuditAsync(ks.Id, user, "document.upload",
                $"Re-uploaded \"{existing.OriginalFilename}\" (deduped)",
                BuildDocumentDetail(existing.LegacyId, existing.Sha256, dedup: true),
                ct).ConfigureAwait(false);
            return Project(existing);
        }

        var nextLegacy = await NextDocumentLegacyIdAsync(ct).ConfigureAwait(false);
        var doc = new DocumentEntity
        {
            LegacyId = nextLegacy,
            KnowledgeSystemId = ks.Id,
            Sha256 = blob.Sha256,
            OriginalFilename = fileName,
            Folder = NormalizeFolder(folder),
            Ext = ext,
            Mime = mime,
            SizeBytes = sizeBytes,
            StoragePath = blob.LegacyStoragePath,
            UploadedAt = _clock.GetUtcNow(),
            ParseStatus = "pending",
            ChunkCount = 0,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(ks.Id, user, "document.upload",
            $"Uploaded \"{doc.OriginalFilename}\"",
            BuildDocumentDetail(doc.LegacyId, doc.Sha256, dedup: false),
            ct).ConfigureAwait(false);
        return Project(doc);
    }

    /// <summary>Fetch a single document by wire LegacyId. Null on miss.</summary>
    public async Task<DocumentOut?> GetAsync(long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;
        return Project(doc);
    }

    /// <summary>
    /// Move / rename. Only non-null fields are applied. Matches Python
    /// <c>move_document</c>.
    /// </summary>
    public async Task<DocumentOut?> MoveAsync(
        long ksId, long documentId, MoveRequest req, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;

        if (req.Folder is not null)
        {
            doc.Folder = NormalizeFolder(req.Folder);
        }
        if (req.OriginalFilename is not null && !string.IsNullOrWhiteSpace(req.OriginalFilename))
        {
            doc.OriginalFilename = req.OriginalFilename.Trim();
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(ks.Id, user, "document.move",
            $"Moved \"{doc.OriginalFilename}\" to {doc.Folder}",
            BuildDocumentDetail(doc.LegacyId, doc.Folder), ct).ConfigureAwait(false);
        return Project(doc);
    }

    // ----------------------------------------------------------------------
    // Parse flow
    // ----------------------------------------------------------------------

    /// <summary>
    /// Parse a single document: stream from blob store → <see cref="IDocumentParser"/>
    /// → <see cref="Chunker"/> → replace old chunks + drop provenance for
    /// the replaced chunks → persist. Mirrors Python
    /// <c>_parse_document</c> (documents.py:192-260).
    /// </summary>
    public async Task<ParseResponse?> ParseAsync(long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        await EnsureNoActiveExtractionAsync(ks.Id, ct).ConfigureAwait(false);

        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;

        return await ParseDocumentAsync(doc, ks, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Batch parse by explicit document IDs and/or folder selectors.
    /// Mirrors Python <c>parse_documents_batch</c>.
    /// </summary>
    public async Task<ParseBatchResponse?> ParseBatchAsync(
        long ksId, ParseBatchIn body, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        await EnsureNoActiveExtractionAsync(ks.Id, ct).ConfigureAwait(false);

        var selectors = new List<System.Linq.Expressions.Expression<Func<DocumentEntity, bool>>>(capacity: 2);

        var docIds = (body.DocumentIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (docIds.Count > 0)
        {
            selectors.Add(d => docIds.Contains(d.LegacyId));
        }

        var folders = (body.Folders ?? Array.Empty<string>())
            .Select(f => f?.Trim() ?? string.Empty)
            .Where(f => f.Length > 0)
            .Distinct()
            .ToList();
        foreach (var rawFolder in folders)
        {
            var normFolder = NormalizeFolder(rawFolder);
            if (body.Recursive)
            {
                var prefix = normFolder == "/" ? "/" : normFolder + "/";
                selectors.Add(d => d.Folder == normFolder || d.Folder.StartsWith(prefix));
            }
            else
            {
                selectors.Add(d => d.Folder == normFolder);
            }
        }

        if (selectors.Count == 0)
        {
            throw new InvalidOperationException("Select at least one document or folder.");
        }

        var combined = selectors.Aggregate(
            (System.Linq.IQueryable<DocumentEntity>)_db.Documents,
            (qAcc, c) => qAcc.Where(c));

        var documents = await combined.ToListAsync(ct).ConfigureAwait(false);

        var items = new List<ParseResponse>(documents.Count);
        var parsed = 0;
        var failed = 0;
        foreach (var doc in documents)
        {
            var pr = await ParseDocumentAsync(doc, ks, user, ct).ConfigureAwait(false);
            items.Add(pr);
            if (pr.ParseStatus == "parsed") parsed++;
            else if (pr.ParseStatus == "failed") failed++;
        }

        return new ParseBatchResponse(items, items.Count, parsed, failed);
    }

    // ----------------------------------------------------------------------
    // Chunks + contribution + impact
    // ----------------------------------------------------------------------

    /// <summary>List chunks for a document in <see cref="ChunkEntity.Idx"/> order.</summary>
    public async Task<IReadOnlyList<ChunkOut>?> ListChunksAsync(
        long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;

        var chunks = await _db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == doc.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        chunks.Sort((a, b) => a.Idx.CompareTo(b.Idx));
        return chunks.Select(c => new ChunkOut(
            Id: c.LegacyId,
            DocumentId: doc.LegacyId,
            Idx: c.Idx,
            Text: c.Text,
            CharStart: c.CharStart,
            CharEnd: c.CharEnd,
            TokenEstimate: c.TokenEstimate,
            CreatedAt: c.CreatedAt)).ToList();
    }

    /// <summary>
    /// Distinct axiom keys + individual IRIs that trace back to this
    /// document's chunks. Mirrors Python <c>document_contribution</c>.
    /// </summary>
    public async Task<ContributionOut?> ContributionAsync(
        long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;

        var chunkIds = await _db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == doc.Id)
            .Select(c => c.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (chunkIds.Count == 0)
        {
            return new ContributionOut(documentId, 0, 0, 0);
        }

        var axiomCount = await _db.AxiomProvenances.AsNoTracking()
            .Where(p => p.KnowledgeSystemId == ks.Id && chunkIds.Contains(p.ChunkId!.Value))
            .Select(p => p.AxiomKey)
            .Distinct()
            .LongCountAsync(ct)
            .ConfigureAwait(false);

        var individualCount = await _db.EntityResolutions.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ks.Id
                && r.SourceChunkId.HasValue
                && chunkIds.Contains(r.SourceChunkId!.Value)
                && (r.Status == "new" || r.Status == "matched")
                && r.IndividualIri != null)
            .Select(r => r.IndividualIri)
            .Distinct()
            .LongCountAsync(ct)
            .ConfigureAwait(false);

        return new ContributionOut(documentId, chunkIds.Count, (int)axiomCount, (int)individualCount);
    }

    /// <summary>
    /// Block 4 returns the placeholder shape (empty <c>systems</c> list).
    /// Block 6 will populate it via the real <c>_document_impact</c>
    /// computation that walks <see cref="AxiomProvenanceEntity"/> rows
    /// and identifies sole-source axioms.
    /// </summary>
    public async Task<ImpactOut?> ImpactAsync(long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return null;
        return new ImpactOut(documentId, Array.Empty<ImpactSystem>());
    }

    // ----------------------------------------------------------------------
    // Delete with cross-KS blob ref-count
    // ----------------------------------------------------------------------

    /// <summary>
    /// Hard delete: cascade-delete provenance + chunks + document row;
    /// remove the physical blob iff no other <see cref="DocumentEntity"/>
    /// in any KS still references the same sha256. Mirrors Python
    /// <c>delete_document</c> (documents.py:462-510) minus the RDF
    /// retraction step (Block 6 territory).
    /// </summary>
    public async Task<bool> DeleteAsync(long ksId, long documentId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await RequireRoleAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return false;
        await EnsureNoActiveExtractionAsync(ks.Id, ct).ConfigureAwait(false);

        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.LegacyId == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null || doc.KnowledgeSystemId != ks.Id) return false;

        var filename = doc.OriginalFilename;
        var docId = doc.Id;
        var oldSha = doc.Sha256;

        // Cascade provenance for this doc's chunks.
        var chunkIds = await _db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == docId)
            .Select(c => c.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (chunkIds.Count > 0)
        {
            await _db.AxiomProvenances
                .Where(p => p.ChunkId.HasValue && chunkIds.Contains(p.ChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await _db.AboxProvenances
                .Where(p => p.ChunkId.HasValue && chunkIds.Contains(p.ChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            // EntityResolutions also FK back to chunks (SourceChunkId)
            // with DeleteBehavior.Restrict, so the chunk DELETE would
            // otherwise fail. Clear them too.
            await _db.EntityResolutions
                .Where(r => r.SourceChunkId.HasValue && chunkIds.Contains(r.SourceChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        await _db.Chunks
            .Where(c => c.DocumentId == docId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Use ExecuteDeleteAsync instead of change-tracker Remove so the
        // SQL-side cascade isn't blocked by chunk entities still tracked
        // from a prior parse on the same DbContext (the FK is required
        // non-nullable and EF Core refuses to "sever" the relationship
        // when child rows already exist in the change tracker but not
        // the database).
        await _db.Documents
            .Where(d => d.Id == docId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Cross-KS ref-count: only physically delete the blob if no
        // other Document row (in any KS) still references this sha.
        var anyOther = await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.Sha256 == oldSha, ct)
            .ConfigureAwait(false);
        if (!anyOther)
        {
            try
            {
                await _blobs.RemoveAsync(oldSha, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort: blob already gone (concurrent delete) is
                // not a failure — log and continue. A genuine I/O error
                // also shouldn't block the audit log.
                _logger?.LogWarning(ex,
                    "Failed to remove orphaned blob {Sha} after deleting document {DocId}",
                    oldSha, documentId);
            }
        }

        await WriteAuditAsync(ks.Id, user, "document.delete",
            $"Deleted document \"{filename}\"",
            BuildDocumentDetail(documentId, oldSha), ct).ConfigureAwait(false);
        return true;
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    /// <summary>
    /// Resolve the actor's user + the requested KS in one round trip and
    /// enforce the minimum role. Returns null tuple when the user can't
    /// be found OR the role is below <paramref name="minimum"/>; the
    /// dispatcher maps null to a 404 to mirror Python's
    /// <c>_doc_in_ks</c> raising <c>HTTPException(404)</c>.
    /// </summary>
    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> RequireRoleAsync(
        long ksId, Actor actor, KSRole minimum, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
        if (user is null) return (null, null);

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.LegacyId == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null) return (null, null);

        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < minimum)
        {
            return (null, null);
        }
        return (user, ks);
    }

    /// <summary>
    /// Reject the operation when an extraction is in flight for this KS.
    /// Mirrors Python <c>extraction_active</c>. <see cref="GraphWriteCoordinator"/>
    /// raises <see cref="Ontology.GraphWriteConflictException"/> when two
    /// writers race for the same graph; we use the same exception type
    /// so the existing <c>FastApiErrorMiddleware</c> maps it to a 409
    /// envelope.
    /// </summary>
    private async Task EnsureNoActiveExtractionAsync(Guid ksId, CancellationToken ct)
    {
        var active = await _db.ExtractionJobs.AsNoTracking()
            .AnyAsync(j => j.KnowledgeSystemId == ksId
                && (j.Status == "pending" || j.Status == "running"), ct)
            .ConfigureAwait(false);
        if (active)
        {
            // Maps to HTTP 409 via FastApiErrorMiddleware — same envelope
            // shape the brief's "抽取进行中的修改返回 409" requirement
            // mandates (we don't have a job id to surface yet because
            // Block 5 will own the extraction lifecycle).
            throw new GraphWriteConflictException(
                "An extraction is in progress; try again after it finishes.");
        }
    }

    /// <summary>
    /// Run the parser → chunker → chunk replacement pipeline for one
    /// document. Always returns a <see cref="ParseResponse"/>; on failure
    /// the response has <c>parse_status == "failed"</c> and a non-null
    /// <c>error</c>.
    /// </summary>
    private async Task<ParseResponse> ParseDocumentAsync(
        DocumentEntity doc, KnowledgeSystemEntity ks, UserEntity user, CancellationToken ct)
    {
        var docId = doc.Id;
        var stream = await _blobs.GetAsync(doc.Sha256, ct).ConfigureAwait(false);
        if (stream is null)
        {
            throw new InvalidOperationException("Blob missing on disk");
        }

        await using (stream)
        {
            try
            {
                var parsed = _parser.Parse(stream, doc.OriginalFilename);
                var spans = _chunker.Chunk(parsed.Text);

                await ApplyChunksAsync(doc, spans, ct).ConfigureAwait(false);

                doc.ParseStatus = "parsed";
                doc.ParserBackend = parsed.Backend;
                doc.ParseError = null;
                doc.TextCharCount = parsed.Text.Length;
                doc.ChunkCount = spans.Count;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                await WriteAuditAsync(ks.Id, user, "document.parse",
                    $"Parsed \"{doc.OriginalFilename}\" ({doc.ChunkCount} chunks)",
                    BuildDocumentDetail(doc.LegacyId, doc.ChunkCount),
                    ct).ConfigureAwait(false);

                return new ParseResponse(
                    DocumentId: doc.LegacyId,
                    ParseStatus: doc.ParseStatus,
                    ParserBackend: parsed.Backend,
                    TextCharCount: doc.TextCharCount,
                    ChunkCount: doc.ChunkCount,
                    Error: null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Parse failed for document {DocId} ({Filename})",
                    doc.LegacyId, doc.OriginalFilename);

                // Detach any pending changes to avoid poisoning SaveChanges.
                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                {
                    if (entry.State == EntityState.Added
                        || entry.State == EntityState.Modified
                        || entry.State == EntityState.Deleted)
                    {
                        entry.State = EntityState.Detached;
                    }
                }

                var refreshed = await _db.Documents
                    .FirstOrDefaultAsync(d => d.Id == docId, ct)
                    .ConfigureAwait(false);
                if (refreshed is not null)
                {
                    refreshed.ParseStatus = "failed";
                    refreshed.ParseError = ex.Message;
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return new ParseResponse(
                    DocumentId: doc.LegacyId,
                    ParseStatus: "failed",
                    ParserBackend: null,
                    TextCharCount: null,
                    ChunkCount: 0,
                    Error: ex.Message);
            }
        }
    }

    /// <summary>
    /// Idempotent chunk replacement: drop old chunks + provenance
    /// pointing at those chunks, insert fresh chunk rows. Mirrors the
    /// Python <c>_parse_document</c> body (documents.py:204-223).
    /// </summary>
    private async Task ApplyChunksAsync(
        DocumentEntity doc, IReadOnlyList<ChunkSpan> spans, CancellationToken ct)
    {
        var docId = doc.Id;
        var oldChunkIds = await _db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == docId)
            .Select(c => c.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (oldChunkIds.Count > 0)
        {
            await _db.AxiomProvenances
                .Where(p => p.ChunkId.HasValue && oldChunkIds.Contains(p.ChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            await _db.AboxProvenances
                .Where(p => p.ChunkId.HasValue && oldChunkIds.Contains(p.ChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            // EntityResolutions.SourceChunkId also has a Restrict FK to
            // chunks, so they need to be cleared before the chunk DELETE.
            await _db.EntityResolutions
                .Where(r => r.SourceChunkId.HasValue && oldChunkIds.Contains(r.SourceChunkId!.Value))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }
        await _db.Chunks
            .Where(c => c.DocumentId == docId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var nextLegacy = await NextChunkLegacyIdAsync(ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        foreach (var span in spans)
        {
            _db.Chunks.Add(new ChunkEntity
            {
                LegacyId = nextLegacy++,
                DocumentId = docId,
                Idx = span.Idx,
                Text = span.Text,
                CharStart = span.CharStart,
                CharEnd = span.CharEnd,
                TokenEstimate = span.TokenEstimate,
                CreatedAt = now,
            });
        }
    }

    private static DocumentOut Project(DocumentEntity d) => new(
        Id: d.LegacyId,
        KnowledgeSystemId: d.KnowledgeSystemId ?? Guid.Empty,
        Sha256: d.Sha256,
        OriginalFilename: d.OriginalFilename,
        Folder: d.Folder,
        Ext: d.Ext,
        Mime: d.Mime,
        SizeBytes: d.SizeBytes,
        StoragePath: d.StoragePath,
        UploadedAt: d.UploadedAt,
        ParseStatus: d.ParseStatus,
        ParserBackend: d.ParserBackend,
        ParseError: d.ParseError,
        TextCharCount: d.TextCharCount,
        ChunkCount: d.ChunkCount,
        TboxExtractedAt: d.TboxExtractedAt,
        AboxExtractedAt: d.AboxExtractedAt);

    /// <summary>
    /// Mirror of Python <c>_norm_folder</c>: leading slash, no trailing
    /// slash, never empty.
    /// </summary>
    private static string NormalizeFolder(string? folder)
    {
        var cleaned = (folder ?? "/").Trim().Trim('/');
        return cleaned.Length == 0 ? "/" : "/" + cleaned;
    }

    private async Task<long> NextDocumentLegacyIdAsync(CancellationToken ct)
    {
        var max = await _db.Documents.AsNoTracking()
            .Select(d => (long?)d.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        return (max ?? 0L) + 1L;
    }

    private async Task<long> NextChunkLegacyIdAsync(CancellationToken ct)
    {
        var max = await _db.Chunks.AsNoTracking()
            .Select(c => (long?)c.LegacyId)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        return (max ?? 0L) + 1L;
    }

    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, string summary,
        JsonElement? detail, CancellationToken token)
    {
        var nextLegacy = await _db.AuditEvents.AsNoTracking()
            .Select(a => (long?)a.LegacyId)
            .MaxAsync(token)
            .ConfigureAwait(false);
        _db.AuditEvents.Add(new AuditEventEntity
        {
            LegacyId = (nextLegacy ?? 0L) + 1L,
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = summary,
            Detail = detail is null
                ? null
                : JsonDocument.Parse(detail.Value.GetRawText()),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private static JsonElement? BuildDocumentDetail(long documentId, object payload, bool? dedup = null)
    {
        var dict = new Dictionary<string, object?> { ["document_id"] = documentId };
        if (dedup.HasValue) dict["dedup"] = dedup.Value;
        if (payload is string s) dict["sha256"] = s;
        else if (payload is int i) dict["chunk_count"] = i;
        else dict["value"] = payload;
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict));
        return doc.RootElement.Clone();
    }
}