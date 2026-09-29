using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ISEStudio.Application.Documents;
using ISEStudio.Application.Foundation;
using ISEStudio.Authorization;
using ISEStudio.Documents;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class DocumentBlobConcurrencyTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public DocumentBlobConcurrencyTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Upload_waits_for_blob_deletion_before_reusing_same_sha()
    {
        var blobRoot = Path.Combine(Path.GetTempPath(), "isestudio-blob-concurrency", Guid.NewGuid().ToString("N"));
        var blobs = new PausingBlobStore(new LocalCasBlobStore(blobRoot));
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);
        });

        var now = DateTimeOffset.UtcNow;
        var user = new UserEntity
        {
            Username = $"blob-lock-{Guid.NewGuid():N}",
            IsAdmin = true,
            Active = true,
            CreatedAt = now,
        };
        var firstKs = CreateKnowledgeSystem(user.Id, now);
        var secondKs = CreateKnowledgeSystem(user.Id, now);
        await using (var setup = services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            db.Users.Add(user);
            db.KnowledgeSystems.AddRange(firstKs, secondKs);
            await db.SaveChangesAsync();
        }

        var actor = new Actor(user.Id.ToString());
        var bytes = Encoding.UTF8.GetBytes("blob reference race");
        var firstDocument = await UploadAsync(firstKs.Id, bytes, "first.txt");
        var deleteTask = DeleteAsync(firstKs.Id, firstDocument.Id);
        await blobs.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var uploadTask = UploadAsync(secondKs.Id, bytes, "second.txt");
        var uploadCompletedWhileRemovalPaused = false;
        var putCallsWhileRemovalPaused = 0;
        try
        {
            var completed = await Task.WhenAny(uploadTask, Task.Delay(TimeSpan.FromSeconds(1)));
            uploadCompletedWhileRemovalPaused = ReferenceEquals(uploadTask, completed);
            putCallsWhileRemovalPaused = blobs.PutCallCount;
        }
        finally
        {
            blobs.AllowRemove();
        }

        Assert.True(await deleteTask.WaitAsync(TimeSpan.FromSeconds(10)));
        var secondDocument = await uploadTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(uploadCompletedWhileRemovalPaused);
        Assert.Equal(1, putCallsWhileRemovalPaused);
        Assert.Equal(firstDocument.Sha256, secondDocument.Sha256);
        Assert.True(await blobs.ExistsAsync(secondDocument.Sha256, CancellationToken.None));

        var firstSource = new SourceEntity
        {
            KnowledgeSystemId = firstKs.Id,
            Kind = "folder",
            Name = "Concurrent A",
            CreatedAt = now,
        };
        var secondSource = new SourceEntity
        {
            KnowledgeSystemId = firstKs.Id,
            Kind = "folder",
            Name = "Concurrent B",
            CreatedAt = now,
        };
        await using (var sourceScope = services.CreateAsyncScope())
        {
            var db = sourceScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            db.Sources.AddRange(firstSource, secondSource);
            await db.SaveChangesAsync();
        }

        var sameKsUploads = await Task.WhenAll(
            UploadAsync(firstKs.Id, bytes, "source-a.txt", firstSource.Id),
            UploadAsync(firstKs.Id, bytes, "source-b.txt", secondSource.Id));
        Assert.Equal(sameKsUploads[0].Id, sameKsUploads[1].Id);
        await using (var verifyScope = services.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var persisted = await db.Documents.SingleAsync(document => document.KnowledgeSystemId == firstKs.Id);
            Assert.Equal(1, await db.DocumentFileVersions.CountAsync(version => version.DocumentId == persisted.Id));
        }

        var lockDigest = SHA256.HashData(Convert.FromHexString(sameKsUploads[0].Sha256));
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(lockDigest);
        var blockingConnection = await _fixture.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(@lock_key)", blockingConnection))
        {
            command.Parameters.AddWithValue("lock_key", lockKey);
            await command.ExecuteNonQueryAsync();
        }

        var deletingDocumentId = sameKsUploads[0].Id;
        var interleavedDelete = DeleteAsync(firstKs.Id, deletingDocumentId);
        Assert.True(await WaitUntilDocumentLockedAsync(deletingDocumentId));
        var interleavedUpload = UploadAsync(firstKs.Id, bytes, "raced-existing.txt", firstSource.Id);
        var uploadFinishedBeforeUnlock = await Task.WhenAny(
            interleavedUpload, Task.Delay(TimeSpan.FromMilliseconds(250))) == interleavedUpload;
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key)", blockingConnection))
        {
            command.Parameters.AddWithValue("lock_key", lockKey);
            await command.ExecuteScalarAsync();
        }
        await blockingConnection.DisposeAsync();

        Assert.True(await interleavedDelete.WaitAsync(TimeSpan.FromSeconds(10)));
        var reuploaded = await interleavedUpload.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(uploadFinishedBeforeUnlock);
        Assert.Equal(sameKsUploads[0].Sha256, reuploaded.Sha256);
        Assert.True(await blobs.ExistsAsync(reuploaded.Sha256, CancellationToken.None));
        await using (var verifyScope = services.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var remaining = await db.Documents.SingleAsync(document => document.KnowledgeSystemId == firstKs.Id);
            Assert.Equal(reuploaded.Id, remaining.Id);
            Assert.Equal(1, await db.DocumentFileVersions.CountAsync(version => version.DocumentId == remaining.Id));
        }

        if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);

        async Task<DocumentOut> UploadAsync(
            Guid knowledgeSystemId, byte[] content, string fileName, Guid? sourceId = null)
        {
            await using var scope = services.CreateAsyncScope();
            var service = CreateService(scope);
            await using var stream = new MemoryStream(content, writable: false);
            return await service.UploadAsync(knowledgeSystemId, stream, fileName, "text/plain",
                stream.Length, "/", actor, CancellationToken.None, sourceId);
        }

        async Task<bool> DeleteAsync(Guid knowledgeSystemId, Guid documentId)
        {
            await using var scope = services.CreateAsyncScope();
            var service = CreateService(scope);
            return await service.DeleteAsync(knowledgeSystemId, documentId, actor, CancellationToken.None);
        }

        DocumentService CreateService(AsyncServiceScope scope)
        {
            var provider = scope.ServiceProvider;
            var clock = TimeProvider.System;
            return new DocumentService(
                provider.GetRequiredService<ISEStudioDbContext>(),
                clock,
                new KnowledgeSystemAccessService(),
                blobs,
                null!,
                null!,
                new ExtractionJobStore(
                    provider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(), clock));
        }

        async Task<bool> WaitUntilDocumentLockedAsync(Guid documentId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var connection = await _fixture.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT id FROM document WHERE id = @document_id FOR UPDATE NOWAIT", connection, transaction);
                command.Parameters.AddWithValue("document_id", documentId);
                try
                {
                    await command.ExecuteScalarAsync();
                    await transaction.CommitAsync();
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    await transaction.RollbackAsync();
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }

            return false;
        }
    }

    private static KnowledgeSystemEntity CreateKnowledgeSystem(Guid ownerId, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        return new KnowledgeSystemEntity
        {
            PublicId = id.ToString("N"),
            Name = $"Blob locking {id:N}",
            OwnerId = ownerId,
            GraphIri = $"https://example.test/graph/{id:N}",
            BaseIri = $"https://example.test/base/{id:N}#",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private sealed class PausingBlobStore(IBlobStore inner) : IBlobStore
    {
        private readonly TaskCompletionSource _removeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowRemove = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _putCallCount;
        private int _removeCallCount;

        public TaskCompletionSource RemoveStarted => _removeStarted;
        public int PutCallCount => Volatile.Read(ref _putCallCount);

        public Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _putCallCount);
            return inner.PutAsync(content, cancellationToken);
        }

        public Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken)
            => inner.GetAsync(sha256, cancellationToken);

        public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken)
            => inner.ExistsAsync(sha256, cancellationToken);

        public async Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _removeCallCount) == 1)
            {
                _removeStarted.TrySetResult();
                await _allowRemove.Task.WaitAsync(cancellationToken);
            }

            return await inner.RemoveAsync(sha256, cancellationToken);
        }

        public void AllowRemove() => _allowRemove.TrySetResult();
    }
}