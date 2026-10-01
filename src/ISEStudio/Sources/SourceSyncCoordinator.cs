using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISEStudio.Sources;

public sealed class SourceSyncCoordinator
{
    private const int MaxWriteAttempts = 3;

    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly IBlobStore _blobs;
    private readonly IReadOnlyDictionary<string, ISourceAdapter> _adapters;
    private readonly SourceSyncJobStore _jobs;
    private readonly TimeProvider _clock;

    public SourceSyncCoordinator(
        IDbContextFactory<ISEStudioDbContext> contexts,
        IBlobStore blobs,
        IEnumerable<ISourceAdapter> adapters,
        SourceSyncJobStore jobs,
        TimeProvider clock)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(adapter => adapter.Kind, StringComparer.OrdinalIgnoreCase);
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<SourceSyncResult> RunAsync(
        Guid sourceId, Guid jobId, Guid runId, CancellationToken cancellationToken)
    {
        SourceEntity? source;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            source = await db.Sources.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == sourceId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (source is null)
            return new SourceSyncResult(0, 0, ["Source not found."], IsComplete: false);
        if (!_adapters.TryGetValue(source.Kind, out var adapter))
            return new SourceSyncResult(0, 0, ["Source connector is unavailable."], false);

        SourceScan scan;
        try
        {
            scan = await adapter.DiscoverAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new SourceSyncResult(0, 0, ["Source discovery failed."], IsComplete: false);
        }

        var errors = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var enumerationCompleted = false;
        try
        {
            await foreach (var item in scan.Items.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await using var itemContent = item.Content;
                if (!seenKeys.Add(item.ExternalKey))
                {
                    errors.Add("Source scan contains duplicate identities.");
                    continue;
                }

                try
                {
                    ValidateItem(item);
                    await using var staged = await StagedBlobUpload.CreateAsync(item.Content, cancellationToken)
                        .ConfigureAwait(false);
                    var outcome = await UpsertItemAsync(
                        sourceId, jobId, runId, item, staged, cancellationToken).ConfigureAwait(false);
                    added += outcome.Added;
                    updated += outcome.Updated;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    errors.Add("Source item ingestion failed.");
                }
            }
            enumerationCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            errors.Add("Source enumeration failed.");
        }

        var isComplete = scan.IsComplete && enumerationCompleted;
        if (isComplete && errors.Count == 0 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(sourceId, jobId, runId, seenKeys, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                errors.Add("Source missing-item reconciliation failed.");
            }
        }

        return new SourceSyncResult(added, updated, errors, isComplete);
    }

    public Task<SourceItemResult> IngestItemAsync(
        Guid sourceId, SourceItem item, CancellationToken ct)
        => IngestItemAsync(sourceId, item, null, ct);

    internal async Task<SourceItemResult> IngestItemAsync(
        Guid sourceId, SourceItem item,
        Func<ISEStudioDbContext, SourceEntity, CancellationToken, Task<bool>>? guard,
        CancellationToken ct)
    {
        ValidateItem(item);
        await using var staged = await StagedBlobUpload.CreateAsync(item.Content, ct).ConfigureAwait(false);
        return await UpsertItemAsync(sourceId, null, null, item, staged, ct, guard).ConfigureAwait(false);
    }

    private async Task<SourceItemResult> UpsertItemAsync(
        Guid sourceId,
        Guid? jobId,
        Guid? runId,
        SourceItem item,
        StagedBlobUpload staged,
        CancellationToken cancellationToken,
        Func<ISEStudioDbContext, SourceEntity, CancellationToken, Task<bool>>? guard = null)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            SourceEntity source;
            if (jobId is { } syncJobId && runId is { } syncRunId)
            {
                if (!await _jobs.ValidateClaimUnderLockAsync(db, sourceId, syncJobId, syncRunId, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("Source sync claim is no longer current or its lease expired.");
                source = await db.Sources.SingleAsync(itemSource => itemSource.Id == sourceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                SourceEntity? lockedSource;
                if (db.Database.IsNpgsql())
                    lockedSource = await db.Sources.FromSqlInterpolated(
                        $"SELECT * FROM source WHERE id = {sourceId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE source SET id = id WHERE id = {sourceId}", cancellationToken).ConfigureAwait(false);
                    lockedSource = await db.Sources.SingleOrDefaultAsync(itemSource => itemSource.Id == sourceId,
                        cancellationToken).ConfigureAwait(false);
                }
                if (lockedSource is null || (guard is not null
                    ? !await guard(db, lockedSource, cancellationToken).ConfigureAwait(false)
                    : lockedSource.Kind is SourceKind.Api or SourceKind.Statements || !_adapters.ContainsKey(lockedSource.Kind)))
                    throw new SourcePushAuthorizationException();
                source = lockedSource;
            }
            var bindingSnapshot = await db.SourceDocumentBindings.AsNoTracking()
                .SingleOrDefaultAsync(binding => binding.SourceId == sourceId
                    && binding.ExternalKey == item.ExternalKey, cancellationToken)
                .ConfigureAwait(false);
            var candidateId = await FindDocumentIdByShaAsync(
                db, source.KnowledgeSystemId, staged.Sha256, cancellationToken).ConfigureAwait(false);
            var lockIds = new[] { bindingSnapshot?.DocumentId, candidateId }
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .Order()
                .ToArray();
            var lockedDocuments = await LockDocumentsAsync(db, lockIds, cancellationToken).ConfigureAwait(false);
            var binding = await db.SourceDocumentBindings.SingleOrDefaultAsync(
                itemBinding => itemBinding.SourceId == sourceId && itemBinding.ExternalKey == item.ExternalKey,
                cancellationToken).ConfigureAwait(false);
            if (binding?.DocumentId != bindingSnapshot?.DocumentId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var candidateAfterDocumentLock = await FindDocumentIdByShaAsync(
                db, source.KnowledgeSystemId, staged.Sha256, cancellationToken).ConfigureAwait(false);
            if (candidateAfterDocumentLock != candidateId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var blobAttempted = false;
            await using var shaLock = await DocumentBlobReferenceLock.AcquireAsync(
                db, [staged.Sha256], cancellationToken).ConfigureAwait(false);
            var candidateAfterShaLock = await FindDocumentIdByShaAsync(
                db, source.KnowledgeSystemId, staged.Sha256, cancellationToken).ConfigureAwait(false);
            if (candidateAfterShaLock != candidateId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var documentsById = lockedDocuments.ToDictionary(document => document.Id);
                var previousDocument = binding is null
                    ? null
                    : documentsById.GetValueOrDefault(binding.DocumentId)
                        ?? throw new InvalidOperationException("Source binding points to a missing document.");
                var candidateDocument = candidateId is { } id ? documentsById.GetValueOrDefault(id) : null;
                if (candidateId.HasValue && candidateDocument is null)
                    throw new InvalidOperationException("Content-matched document disappeared while locked.");

                if (binding is not null && previousDocument!.Sha256 == staged.Sha256)
                {
                    binding.MissingSince = null;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await RecalculateMissingAsync(db, [previousDocument], _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    await RenewAndCommitAsync(db, transaction, sourceId, jobId, runId, cancellationToken)
                        .ConfigureAwait(false);
                    return new SourceItemResult(0, 0);
                }

                if (binding is not null && candidateDocument is not null)
                {
                    var oldDocument = previousDocument!;
                    ClearPrimaryBinding(oldDocument, sourceId, item.ExternalKey);
                    binding.DocumentId = candidateDocument.Id;
                    binding.MissingSince = null;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await RecalculateMissingAsync(db, [oldDocument, candidateDocument], _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    await RenewAndCommitAsync(db, transaction, sourceId, jobId, runId, cancellationToken)
                        .ConfigureAwait(false);
                    return new SourceItemResult(0, 1);
                }

                var shouldUpdateInPlace = binding is not null
                    && candidateDocument is null
                    && !previousDocument!.IsManualUpload
                    && previousDocument.SourceId == sourceId
                    && previousDocument.ExternalKey == item.ExternalKey
                    && await db.SourceDocumentBindings.AsNoTracking()
                        .CountAsync(existing => existing.DocumentId == previousDocument.Id, cancellationToken)
                        .ConfigureAwait(false) == 1;

                if (shouldUpdateInPlace)
                {
                    var document = previousDocument!;
                    var blob = await WriteBlobAsync(staged, () => blobAttempted = true, cancellationToken)
                        .ConfigureAwait(false);
                    var fileVersion = await UpdateDocumentAsync(
                        db, document, item, staged, blob, cancellationToken).ConfigureAwait(false);
                    binding!.MissingSince = null;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await new DocumentParseQueueService(db, _clock).EnqueueAsync(
                        source.KnowledgeSystemId, document.Id, fileVersion.Id, cancellationToken).ConfigureAwait(false);
                    await RecalculateMissingAsync(db, [document], _clock.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    await RenewAndCommitAsync(db, transaction, sourceId, jobId, runId, cancellationToken)
                        .ConfigureAwait(false);
                    return new SourceItemResult(0, 1);
                }

                var addedDocument = candidateDocument;
                DocumentFileVersionEntity? newFileVersion = null;
                var addedCount = 0;
                if (addedDocument is null)
                {
                    blobAttempted = true;
                    var blob = await WriteBlobAsync(staged, () => blobAttempted = true, cancellationToken)
                        .ConfigureAwait(false);
                    addedDocument = CreateDocument(sourceId, source.KnowledgeSystemId, item, staged, blob);
                    db.Documents.Add(addedDocument);
                    newFileVersion = CreateFileVersion(addedDocument.Id, 1, item, staged);
                    db.DocumentFileVersions.Add(newFileVersion);
                    addedCount = 1;
                }

                if (binding is null)
                {
                    db.SourceDocumentBindings.Add(new SourceDocumentBindingEntity
                    {
                        SourceId = sourceId,
                        ExternalKey = item.ExternalKey,
                        DocumentId = addedDocument.Id,
                    });
                }
                else
                {
                    ClearPrimaryBinding(previousDocument!, sourceId, item.ExternalKey);
                    binding.DocumentId = addedDocument.Id;
                    binding.MissingSince = null;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (newFileVersion is not null)
                {
                    await new DocumentParseQueueService(db, _clock).EnqueueAsync(
                        source.KnowledgeSystemId, addedDocument.Id, newFileVersion.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                var affectedDocuments = previousDocument is null
                    ? new[] { addedDocument }
                    : new[] { previousDocument, addedDocument };
                await RecalculateMissingAsync(db, affectedDocuments, _clock.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                await RenewAndCommitAsync(db, transaction, sourceId, jobId, runId, cancellationToken)
                    .ConfigureAwait(false);
                return new SourceItemResult(addedCount, addedCount == 0 && binding is not null ? 1 : 0);
            }
            catch (DbUpdateException exception) when (IsRetryableConflict(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                if (blobAttempted)
                    await RemoveBlobIfUnreferencedAsync(db, staged.Sha256, CancellationToken.None)
                        .ConfigureAwait(false);
                if (attempt + 1 == MaxWriteAttempts) throw;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                if (blobAttempted)
                    await RemoveBlobIfUnreferencedAsync(db, staged.Sha256, CancellationToken.None)
                        .ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Source item could not be serialized after repeated conflicts.");
    }

    private async Task ReconcileAsync(
        Guid sourceId, Guid jobId, Guid runId, IReadOnlySet<string> seenKeys, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await _jobs.ValidateClaimUnderLockAsync(db, sourceId, jobId, runId, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("Source sync claim expired before missing-item reconciliation.");

        var source = await db.Sources.SingleAsync(item => item.Id == sourceId, cancellationToken)
            .ConfigureAwait(false);
        var initialBindings = await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.SourceId == sourceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var documentIds = initialBindings.Select(binding => binding.DocumentId).Distinct().Order().ToArray();
        var documents = await LockDocumentsAsync(db, documentIds, cancellationToken).ConfigureAwait(false);
        var currentBindings = await db.SourceDocumentBindings
            .Where(binding => binding.SourceId == sourceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        foreach (var binding in currentBindings)
        {
            binding.MissingSince = seenKeys.Contains(binding.ExternalKey)
                ? null
                : binding.MissingSince ?? now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecalculateMissingAsync(db, documents, now, cancellationToken).ConfigureAwait(false);
        await RenewAndCommitAsync(db, transaction, sourceId, jobId, runId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<DocumentEntity>> LockDocumentsAsync(
        ISEStudioDbContext db, Guid[] documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Length == 0) return [];
        var orderedIds = documentIds.Distinct().Order().ToArray();
        if (db.Database.IsNpgsql())
        {
            return await db.Documents.FromSqlInterpolated(
                    $"SELECT * FROM document WHERE id = ANY({orderedIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var documentId in orderedIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE document SET id = id WHERE id = {documentId}", cancellationToken).ConfigureAwait(false);
        }
        return await db.Documents.Where(document => orderedIds.Contains(document.Id))
            .OrderBy(document => document.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> FindDocumentIdByShaAsync(
        ISEStudioDbContext db, Guid? knowledgeSystemId, string sha256, CancellationToken cancellationToken)
        => await db.Documents.AsNoTracking()
            .Where(document => document.KnowledgeSystemId == knowledgeSystemId && document.Sha256 == sha256)
            .Select(document => (Guid?)document.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<BlobWriteResult> WriteBlobAsync(
        StagedBlobUpload staged, Action markAttempted, CancellationToken cancellationToken)
    {
        markAttempted();
        await using var content = staged.OpenRead();
        var blob = await _blobs.PutAsync(content, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(blob.Sha256, staged.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Blob store returned a SHA-256 different from staged source content.");
        return blob;
    }

    private async Task<DocumentFileVersionEntity> UpdateDocumentAsync(
        ISEStudioDbContext db,
        DocumentEntity document,
        SourceItem item,
        StagedBlobUpload staged,
        BlobWriteResult blob,
        CancellationToken cancellationToken)
    {
        var latestVersion = await db.DocumentFileVersions.AsNoTracking()
            .Where(version => version.DocumentId == document.Id)
            .Select(version => (int?)version.Version)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        document.Sha256 = blob.Sha256;
        document.OriginalFilename = item.Filename;
        document.Ext = Path.GetExtension(item.Filename).TrimStart('.').ToLowerInvariant();
        document.Mime = item.Mime;
        document.SizeBytes = staged.SizeBytes;
        document.StoragePath = blob.LegacyStoragePath;
        document.UploadedAt = _clock.GetUtcNow();
        document.ParseStatus = "pending";
        document.ParseError = null;
        document.MissingSince = null;
        var fileVersion = CreateFileVersion(document.Id, latestVersion + 1, item, staged);
        db.DocumentFileVersions.Add(fileVersion);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return fileVersion;
    }

    private DocumentEntity CreateDocument(
        Guid sourceId,
        Guid? knowledgeSystemId,
        SourceItem item,
        StagedBlobUpload staged,
        BlobWriteResult blob)
        => new()
        {
            KnowledgeSystemId = knowledgeSystemId,
            SourceId = sourceId,
            ExternalKey = item.ExternalKey,
            IsManualUpload = false,
            Sha256 = blob.Sha256,
            OriginalFilename = item.Filename,
            Folder = "/",
            Ext = Path.GetExtension(item.Filename).TrimStart('.').ToLowerInvariant(),
            Mime = item.Mime,
            SizeBytes = staged.SizeBytes,
            StoragePath = blob.LegacyStoragePath,
            UploadedAt = _clock.GetUtcNow(),
            ParseStatus = "pending",
            ChunkCount = 0,
        };

    private DocumentFileVersionEntity CreateFileVersion(
        Guid documentId, int version, SourceItem item, StagedBlobUpload staged)
        => new()
        {
            DocumentId = documentId,
            Version = version,
            Sha256 = staged.Sha256,
            SizeBytes = staged.SizeBytes,
            DocTime = item.DocTime,
            CreatedAt = _clock.GetUtcNow(),
        };

    private static async Task RecalculateMissingAsync(
        ISEStudioDbContext db,
        IEnumerable<DocumentEntity> documents,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents.DistinctBy(item => item.Id))
        {
            var bindingStates = await db.SourceDocumentBindings.AsNoTracking()
                .Where(binding => binding.DocumentId == document.Id)
                .Select(binding => binding.MissingSince)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (document.IsManualUpload || bindingStates.Count == 0 || bindingStates.Any(state => state is null))
                document.MissingSince = null;
            else
                document.MissingSince ??= now;
        }
    }

    private static void ClearPrimaryBinding(DocumentEntity document, Guid sourceId, string externalKey)
    {
        if (document.SourceId == sourceId && document.ExternalKey == externalKey)
        {
            document.SourceId = null;
            document.ExternalKey = null;
        }
    }

    private async Task RenewAndCommitAsync(
        ISEStudioDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Guid sourceId,
        Guid? jobId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        if (jobId is { } syncJobId && runId is { } syncRunId
            && !await _jobs.RenewUnderLockAsync(db, sourceId, syncJobId, syncRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Source sync claim changed before item commit.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveBlobIfUnreferencedAsync(
        ISEStudioDbContext db, string sha256, CancellationToken cancellationToken)
    {
        var referenced = await db.Documents.AsNoTracking()
            .AnyAsync(document => document.Sha256 == sha256, cancellationToken).ConfigureAwait(false)
            || await db.DocumentFileVersions.AsNoTracking()
                .AnyAsync(version => version.Sha256 == sha256, cancellationToken).ConfigureAwait(false);
        if (!referenced)
        {
            try
            {
                await _blobs.RemoveAsync(sha256, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static bool IsRetryableConflict(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation,
        };

    private static void ValidateItem(SourceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ExternalKey) || item.ExternalKey.Length > 1024)
            throw new InvalidOperationException("External key must contain 1 to 1024 characters.");
        if (string.IsNullOrWhiteSpace(item.Filename) || Path.GetFileName(item.Filename) != item.Filename)
            throw new InvalidOperationException("Source item filename must be a plain filename.");
        if (string.IsNullOrWhiteSpace(Path.GetExtension(item.Filename)))
            throw new InvalidOperationException("Source item filename must include an extension.");
        ArgumentNullException.ThrowIfNull(item.Content);
    }

}