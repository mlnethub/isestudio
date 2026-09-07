using ISEStudio.Extraction;
using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Documents;

public sealed record PlainTextIngestionJob(
    Guid Id,
    Guid KnowledgeSystemId,
    Guid DocumentId,
    string Content,
    string Model);

public sealed record PlainTextIngestionJobResult(
    string Status,
    DocumentVersionResult Version,
    string? Error);

public sealed class PlainTextIngestionJobProcessor
{
    public const string Kind = "plain_text";

    private readonly ISEStudioDbContext _db;
    private readonly PlainTextIngestionService _ingestion;

    public PlainTextIngestionJobProcessor(
        ISEStudioDbContext db,
        PlainTextIngestionService ingestion)
    {
        _db = db;
        _ingestion = ingestion;
    }

    public async Task<PlainTextIngestionJobResult> ProcessAsync(
        PlainTextIngestionJob input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var job = await _db.ExtractionJobs
            .SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            job = new ExtractionJobEntity
            {
                Id = input.Id,
                KnowledgeSystemId = input.KnowledgeSystemId,
                Kind = Kind,
                Model = input.Model,
                CreatedAt = DateTimeOffset.UtcNow,
                Log = string.Empty,
            };
            _db.ExtractionJobs.Add(job);
        }

        job.Status = JobStatus.Running.ToWire();
        job.Kind = Kind;
        job.Model = input.Model;
        job.Error = null;
        job.FinishedAt = null;
        job.Phase = "plain_text";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var version = await _ingestion.IngestAsync(
                input.KnowledgeSystemId,
                input.DocumentId,
                input.Content,
                cancellationToken).ConfigureAwait(false);

            job.Status = JobStatus.Completed.ToWire();
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.Phase = "finalizing";
            job.ProcessedChunks = version.ChunkCount;
            job.TotalChunks = version.ChunkCount;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PlainTextIngestionJobResult(job.Status, version, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            job.Status = JobStatus.Failed.ToWire();
            job.Phase = "failed";
            job.Error = exception.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}