using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Documents;

public sealed class DocumentParseQueueService
{
    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;

    public DocumentParseQueueService(ISEStudioDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<DocumentParseQueueOut?> EnqueueAsync(
        Guid knowledgeSystemId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await _db.Documents.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == documentId && item.KnowledgeSystemId == knowledgeSystemId,
            cancellationToken).ConfigureAwait(false);
        if (document is null) return null;

        var fileVersions = await _db.DocumentFileVersions.AsNoTracking()
            .Where(version => version.DocumentId == documentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var fileVersion = fileVersions.OrderByDescending(version => version.Version).FirstOrDefault();
        if (fileVersion is null)
            throw new InvalidOperationException("Document has no raw file version to parse.");
        if (!string.Equals(fileVersion.Sha256, document.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Current file version does not match document SHA-256.");

        var job = new DocumentParseJobEntity
        {
            KnowledgeSystemId = knowledgeSystemId,
            DocumentId = documentId,
            SourceId = document.SourceId,
            DocumentFileVersionId = fileVersion.Id,
            Sha256 = fileVersion.Sha256,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.DocumentParseJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentParseQueueOut(job.Id, job.Status, documentId, fileVersion.Id);
    }
}