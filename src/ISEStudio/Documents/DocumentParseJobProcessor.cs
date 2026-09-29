using System.Security.Cryptography;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Documents;

public sealed class DocumentParseJobProcessor
{
    private readonly ISEStudioDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly IDocumentParser _parser;
    private readonly PlainTextIngestionService _ingestion;
    private readonly TimeProvider _clock;

    public DocumentParseJobProcessor(ISEStudioDbContext db, IBlobStore blobs,
        IDocumentParser parser, PlainTextIngestionService ingestion, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _parser = parser;
        _ingestion = ingestion;
        _clock = clock;
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.DocumentParseJobs.SingleAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false);
        if (job.Status == "completed") return;
        if (job.Status != "pending" && job.Status != "running")
            throw new InvalidOperationException("Document parse job is not pending.");
        job.Status = "running";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var document = await _db.Documents.SingleAsync(item => item.Id == job.DocumentId
                && item.KnowledgeSystemId == job.KnowledgeSystemId, cancellationToken).ConfigureAwait(false);
            var version = await _db.DocumentFileVersions.SingleAsync(item => item.Id == job.DocumentFileVersionId
                && item.DocumentId == document.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(version.Sha256, job.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Queued file version SHA-256 does not match the job.");

            await using var blob = await _blobs.GetAsync(job.Sha256, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Queued document blob was not found.");
            await using var buffer = new FileStream(
                Path.Combine(Path.GetTempPath(), $"isestudio-parse-{Guid.NewGuid():N}.blob"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await blob.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            var actualSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, cancellationToken)
                .ConfigureAwait(false));
            if (!string.Equals(actualSha, job.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Queued document blob SHA-256 does not match the job.");
            buffer.Position = 0;
            var parsed = _parser.Parse(buffer, document.OriginalFilename);
            var snapshot = await _ingestion.IngestAsync(job.KnowledgeSystemId, document.Id,
                parsed.Text, cancellationToken, version.Id).ConfigureAwait(false);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (_db.Database.IsNpgsql())
                await _db.Documents.FromSqlInterpolated(
                    $"SELECT * FROM document WHERE id = {document.Id} FOR UPDATE")
                    .AsNoTracking().ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            await _db.Entry(document).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var latest = await _db.DocumentFileVersions.Where(item => item.DocumentId == document.Id)
                .OrderByDescending(item => item.Version).FirstAsync(cancellationToken).ConfigureAwait(false);
            if (latest.Id == version.Id && document.Sha256 == version.Sha256)
            {
                document.ParseStatus = "parsed";
                document.ParseError = null;
                document.ParserBackend = parsed.Backend;
                document.ParserVersion = parsed.ParserVersion;
                document.Mime = parsed.MediaType ?? document.Mime;
                document.TextCharCount = parsed.Text.EnumerateRunes().Count();
                document.ChunkCount = snapshot.ChunkCount;
            }
            job.Status = "completed";
            job.FinishedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            var failed = await _db.DocumentParseJobs.SingleAsync(item => item.Id == jobId, CancellationToken.None)
                .ConfigureAwait(false);
            await using var transaction = await _db.Database.BeginTransactionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (_db.Database.IsNpgsql())
                await _db.Documents.FromSqlInterpolated(
                    $"SELECT * FROM document WHERE id = {failed.DocumentId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(CancellationToken.None).ConfigureAwait(false);
            var document = await _db.Documents.SingleOrDefaultAsync(item => item.Id == failed.DocumentId,
                CancellationToken.None).ConfigureAwait(false);
            if (document is not null)
            {
                var latest = await _db.DocumentFileVersions.Where(item => item.DocumentId == document.Id)
                    .OrderByDescending(item => item.Version).FirstOrDefaultAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (latest?.Id == failed.DocumentFileVersionId && document.Sha256 == failed.Sha256)
                {
                    document.ParseStatus = "failed";
                    document.ParseError = exception.Message;
                }
            }
            failed.Status = "failed";
            failed.Error = exception.Message;
            failed.FinishedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}