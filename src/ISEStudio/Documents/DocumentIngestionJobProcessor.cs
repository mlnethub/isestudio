using System.Text;
using ISEStudio.Application.Documents;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Documents;

public sealed record DocumentIngestionJob(
    Guid Id,
    Guid KnowledgeSystemId,
    Guid DocumentId,
    string Model);

public sealed record DocumentIngestionJobResult(
    string Status,
    DocumentVersionResult Version,
    string? Error);

public sealed class DocumentIngestionJobProcessor
{
    public const string Kind = PlainTextIngestionJobProcessor.Kind;

    private readonly ISEStudioDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly IDocumentParser _parser;
    private readonly PlainTextIngestionService _ingestion;

    public DocumentIngestionJobProcessor(
        ISEStudioDbContext db,
        IBlobStore blobs,
        IDocumentParser parser,
        PlainTextIngestionService ingestion)
    {
        _db = db;
        _blobs = blobs;
        _parser = parser;
        _ingestion = ingestion;
    }

    public async Task<DocumentIngestionJobResult> ProcessAsync(
        DocumentIngestionJob input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var job = await LoadOrCreateJobAsync(input, cancellationToken).ConfigureAwait(false);
        DocumentEntity? document = null;
        try
        {
            document = await _db.Documents.SingleOrDefaultAsync(
                item => item.Id == input.DocumentId,
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                throw new InvalidOperationException($"Document '{input.DocumentId}' was not found.");
            }

            if (document.KnowledgeSystemId != input.KnowledgeSystemId)
            {
                throw new InvalidOperationException("Document does not belong to the knowledge system.");
            }

            job.Status = JobStatus.Running.ToWire();
            job.Model = input.Model;
            job.Error = null;
            job.FinishedAt = null;
            job.Phase = "parsing";
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await using var blob = await _blobs.GetAsync(document.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (blob is null)
            {
                throw new InvalidOperationException(
                    $"Blob '{document.Sha256}' for document '{document.Id}' was not found.");
            }

            var parsed = _parser.Parse(blob, document.OriginalFilename);
            var version = await _ingestion.IngestAsync(
                input.KnowledgeSystemId,
                input.DocumentId,
                parsed.Text,
                cancellationToken).ConfigureAwait(false);

            document.ParseStatus = "parsed";
            document.ParserBackend = parsed.Backend;
            document.ParseError = null;
            document.TextCharCount = parsed.Text.EnumerateRunes().Count();
            document.ChunkCount = version.ChunkCount;
            job.Status = JobStatus.Completed.ToWire();
            job.Phase = "finalizing";
            job.ProcessedChunks = version.ChunkCount;
            job.TotalChunks = version.ChunkCount;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentIngestionJobResult(job.Status, version, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (document is not null && document.KnowledgeSystemId == input.KnowledgeSystemId)
            {
                document.ParseStatus = "failed";
                document.ParseError = exception.Message;
                await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            job.Status = JobStatus.Failed.ToWire();
            job.Phase = "failed";
            job.Error = exception.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ExtractionJobEntity> LoadOrCreateJobAsync(
        DocumentIngestionJob input,
        CancellationToken cancellationToken)
    {
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
            return job;
        }

        if (job.KnowledgeSystemId != input.KnowledgeSystemId)
        {
            throw new InvalidOperationException(
                $"Extraction job '{input.Id}' belongs to another knowledge system.");
        }

        if (!string.Equals(job.Kind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Extraction job '{input.Id}' has kind '{job.Kind}', not '{Kind}'.");
        }

        return job;
    }
}