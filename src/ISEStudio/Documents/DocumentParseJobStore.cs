using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Documents;

public sealed class DocumentParseJobStore
{
    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;

    public DocumentParseJobStore(IDbContextFactory<ISEStudioDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task<DocumentParseJobEntity?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var lockClause = db.Database.IsNpgsql() ? "FOR UPDATE SKIP LOCKED" : string.Empty;
        var sql = $"""
            UPDATE document_parse_job SET status = 'running'
            WHERE id = (
                SELECT id FROM document_parse_job WHERE status = 'pending'
                ORDER BY created_at, id {lockClause} LIMIT 1
            ) AND status = 'pending'
            RETURNING *
            """;
        var rows = await db.DocumentParseJobs.FromSqlRaw(sql).AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task MarkFailedAsync(Guid jobId, string error, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.DocumentParseJobs.SingleAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false);
        if (job.Status is "completed" or "failed") return;
        job.Status = "failed";
        job.Error = error;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}