using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace ISEStudio.Documents;

public sealed class DocumentVersionStore
{
    private readonly ISEStudioDbContext _db;

    public DocumentVersionStore(ISEStudioDbContext db)
    {
        _db = db;
    }

    public async Task<DocumentVersionResult> RecordAsync(
        DocumentVersionInput input,
        CancellationToken cancellationToken)
    {
        var contentSha256 = NormalizeSha256(input.ContentSha256);
        if (input.KnowledgeSystemId == Guid.Empty || input.DocumentId == Guid.Empty)
        {
            throw new ArgumentException("Knowledge system and document are required.", nameof(input));
        }

        var documentExists = await _db.Documents.AnyAsync(
            item => item.Id == input.DocumentId
                && item.KnowledgeSystemId == input.KnowledgeSystemId,
            cancellationToken).ConfigureAwait(false);
        if (!documentExists)
        {
            throw new InvalidOperationException("Document does not belong to the knowledge system.");
        }

        if (input.FileVersionId is { } fileVersionId && !await _db.DocumentFileVersions.AnyAsync(
                file => file.Id == fileVersionId && file.DocumentId == input.DocumentId,
                cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("File version does not belong to the document.");

        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var existing = await _db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == input.KnowledgeSystemId
                && item.DocumentId == input.DocumentId
                && item.ContentSha256 == contentSha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await LinkFileVersionAsync(input.FileVersionId, existing.Id, cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Project(existing);
        }

        existing = await _db.DocumentVersions.SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == input.KnowledgeSystemId
                && item.DocumentId == input.DocumentId
                && item.ContentSha256 == contentSha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await LinkFileVersionAsync(input.FileVersionId, existing.Id, cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Project(existing);
        }

        var versionId = Guid.NewGuid();
        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO document_version
                (id, knowledge_system_id, document_id, content_sha256, chunk_count, created_at)
            VALUES
                ({versionId}, {input.KnowledgeSystemId}, {input.DocumentId}, {contentSha256}, {input.Chunks.Count}, {DateTimeOffset.UtcNow})
            ON CONFLICT (knowledge_system_id, document_id, content_sha256) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            existing = await _db.DocumentVersions.AsNoTracking().SingleAsync(
                item => item.KnowledgeSystemId == input.KnowledgeSystemId
                    && item.DocumentId == input.DocumentId
                    && item.ContentSha256 == contentSha256,
                cancellationToken).ConfigureAwait(false);
            await LinkFileVersionAsync(input.FileVersionId, existing.Id, cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Project(existing);
        }

        _db.DocumentVersionChunks.AddRange(input.Chunks.Select(chunk => new DocumentVersionChunkEntity
        {
            DocumentVersionId = versionId,
            Idx = chunk.Index,
            Text = chunk.Text,
            CharStart = chunk.CharStart,
            CharEnd = chunk.CharEnd,
            TokenEstimate = chunk.TokenEstimate,
        }));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await LinkFileVersionAsync(input.FileVersionId, versionId, cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentVersionResult(
            versionId,
            input.KnowledgeSystemId,
            input.DocumentId,
            contentSha256,
            input.Chunks.Count);
    }

    private async Task LinkFileVersionAsync(Guid? fileVersionId, Guid documentVersionId, CancellationToken ct)
    {
        if (fileVersionId is null) return;
        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO document_file_version_snapshot
                    (id, document_file_version_id, document_version_id)
                VALUES ({Guid.NewGuid()}, {fileVersionId.Value}, {documentVersionId})
                ON CONFLICT (document_file_version_id, document_version_id) DO NOTHING
                """, ct).ConfigureAwait(false);
        }
        else if (!await _db.DocumentFileVersionSnapshots.AnyAsync(
                     link => link.DocumentFileVersionId == fileVersionId && link.DocumentVersionId == documentVersionId,
                     ct).ConfigureAwait(false))
        {
            _db.DocumentFileVersionSnapshots.Add(new DocumentFileVersionSnapshotEntity
            {
                DocumentFileVersionId = fileVersionId.Value,
                DocumentVersionId = documentVersionId,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static string NormalizeSha256(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Content SHA-256 must be exactly 64 hexadecimal characters.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static DocumentVersionResult Project(DocumentVersionEntity version) => new(
        version.Id,
        version.KnowledgeSystemId,
        version.DocumentId,
        version.ContentSha256,
        version.ChunkCount);
}