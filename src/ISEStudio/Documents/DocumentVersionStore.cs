using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ContentSha256);
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

        var existing = await _db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == input.KnowledgeSystemId
                && item.DocumentId == input.DocumentId
                && item.ContentSha256 == input.ContentSha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Project(existing);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        existing = await _db.DocumentVersions.SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == input.KnowledgeSystemId
                && item.DocumentId == input.DocumentId
                && item.ContentSha256 == input.ContentSha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Project(existing);
        }

        var version = new DocumentVersionEntity
        {
            KnowledgeSystemId = input.KnowledgeSystemId,
            DocumentId = input.DocumentId,
            ContentSha256 = input.ContentSha256,
            ChunkCount = input.Chunks.Count,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DocumentVersions.Add(version);
        _db.DocumentVersionChunks.AddRange(input.Chunks.Select(chunk => new DocumentVersionChunkEntity
        {
            DocumentVersionId = version.Id,
            Idx = chunk.Index,
            Text = chunk.Text,
            CharStart = chunk.CharStart,
            CharEnd = chunk.CharEnd,
            TokenEstimate = chunk.TokenEstimate,
        }));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Project(version);
    }

    private static DocumentVersionResult Project(DocumentVersionEntity version) => new(
        version.Id,
        version.KnowledgeSystemId,
        version.DocumentId,
        version.ContentSha256,
        version.ChunkCount);
}