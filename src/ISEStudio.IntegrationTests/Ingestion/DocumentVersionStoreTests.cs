using ISEStudio.Documents;
using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class DocumentVersionStoreTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public DocumentVersionStoreTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Two_raw_versions_can_share_one_parsed_snapshot()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var doc = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var first = new DocumentFileVersionEntity
        {
            DocumentId = doc.Id, Version = 1, Sha256 = new string('1', 64),
            SizeBytes = 10, CreatedAt = DateTimeOffset.UtcNow,
        };
        var second = new DocumentFileVersionEntity
        {
            DocumentId = doc.Id, Version = 2, Sha256 = new string('2', 64),
            SizeBytes = 11, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.AddRange(first, second);
        await db.SaveChangesAsync();
        var store = new DocumentVersionStore(db);
        var parsedSha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var input = new DocumentVersionInput(_fixture.KnowledgeSystemId, doc.Id, parsedSha,
            [new DocumentVersionChunkInput(0, "same", 0, 4, 1)], first.Id);
        var initial = await store.RecordAsync(input, CancellationToken.None);
        var repeated = await store.RecordAsync(input with { FileVersionId = second.Id }, CancellationToken.None);
        Assert.Equal(initial.Id, repeated.Id);
        Assert.Equal(2, await db.DocumentFileVersionSnapshots.CountAsync(link => link.DocumentVersionId == initial.Id));
        await store.RecordAsync(input, CancellationToken.None);
        Assert.Equal(2, await db.DocumentFileVersionSnapshots.CountAsync(link => link.DocumentVersionId == initial.Id));
    }

    [Fact]
    public async Task Outer_transaction_rollback_removes_snapshot_and_file_link()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var doc = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var fileVersion = new DocumentFileVersionEntity
        {
            DocumentId = doc.Id, Version = 3, Sha256 = new string('3', 64),
            SizeBytes = 12, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(fileVersion);
        await db.SaveChangesAsync();

        var parsedSha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        Guid snapshotId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var result = await new DocumentVersionStore(db).RecordAsync(
                new DocumentVersionInput(_fixture.KnowledgeSystemId, doc.Id, parsedSha,
                    [new DocumentVersionChunkInput(0, "rollback", 0, 8, 1)], fileVersion.Id),
                CancellationToken.None);
            snapshotId = result.Id;
            Assert.True(await db.DocumentFileVersionSnapshots.AnyAsync(link => link.DocumentVersionId == snapshotId));
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        Assert.False(await db.DocumentVersions.AnyAsync(version => version.Id == snapshotId));
        Assert.False(await db.DocumentFileVersionSnapshots.AnyAsync(link => link.DocumentVersionId == snapshotId));
    }

    [Fact]
    public async Task Same_document_version_is_idempotent_and_chunks_keep_version_ownership()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var store = new DocumentVersionStore(db);
        var chunks = new[]
        {
            new DocumentVersionChunkInput(0, "first", 0, 5, 1),
            new DocumentVersionChunkInput(1, "second", 5, 11, 1),
        };
        var input = new DocumentVersionInput(
            _fixture.KnowledgeSystemId,
            document.Id,
            new string('a', 64),
            chunks);

        var first = await store.RecordAsync(input, CancellationToken.None);
        var second = await store.RecordAsync(input, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync());
        Assert.Equal(2, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Id));
        Assert.All(await db.DocumentVersionChunks.ToListAsync(), item =>
            Assert.Equal(first.Id, item.DocumentVersionId));
    }

    [Fact]
    public async Task Concurrent_same_hash_with_different_chunks_keeps_one_complete_winner()
    {
        await using var seedServices = _fixture.BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await seedDb.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var contentSha256 = new string('b', 64);
        var firstChunks = new[]
        {
            new DocumentVersionChunkInput(0, "first winner", 0, 12, 2),
            new DocumentVersionChunkInput(1, "first metadata", 12, 26, 3),
            new DocumentVersionChunkInput(2, "first tail", 26, 36, 2),
        };
        var secondChunks = new[]
        {
            new DocumentVersionChunkInput(0, "second winner", 100, 113, 4),
            new DocumentVersionChunkInput(1, "second metadata", 113, 128, 5),
        };
        var firstInput = new DocumentVersionInput(
            _fixture.KnowledgeSystemId,
            document.Id,
            contentSha256,
            firstChunks);
        var secondInput = firstInput with { Chunks = secondChunks };
        var barrier = new Barrier(2);

        async Task<DocumentVersionResult> RecordFromSeparateContextAsync(DocumentVersionInput input)
        {
            await using var services = _fixture.BuildServices();
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            await Task.Run(() => barrier.SignalAndWait());
            return await new DocumentVersionStore(db).RecordAsync(input, CancellationToken.None);
        }

        var results = await Task.WhenAll(
            RecordFromSeparateContextAsync(firstInput),
            RecordFromSeparateContextAsync(secondInput));

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(
            1,
            await seedDb.DocumentVersions.CountAsync(item => item.ContentSha256 == contentSha256));

        var actualChunks = (await seedDb.DocumentVersionChunks
                .AsNoTracking()
                .Where(item => item.DocumentVersionId == results[0].Id)
                .OrderBy(item => item.Idx)
                .ToListAsync())
            .Select(item => (item.Idx, item.Text, item.CharStart, item.CharEnd, item.TokenEstimate))
            .ToList();
        var firstExpected = firstChunks
            .Select(item => (item.Index, item.Text, item.CharStart, item.CharEnd, item.TokenEstimate))
            .ToList();
        var secondExpected = secondChunks
            .Select(item => (item.Index, item.Text, item.CharStart, item.CharEnd, item.TokenEstimate))
            .ToList();

        Assert.True(
            actualChunks.SequenceEqual(firstExpected) || actualChunks.SequenceEqual(secondExpected),
            $"Concurrent winner chunks were not one complete payload: {string.Join("; ", actualChunks)}");
    }

    [Fact]
    public async Task Version_cannot_be_recorded_for_a_document_in_another_knowledge_system()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var store = new DocumentVersionStore(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordAsync(
            new DocumentVersionInput(
                Guid.NewGuid(),
                document.Id,
                new string('e', 64),
                [new DocumentVersionChunkInput(0, "secret", 0, 6, 1)]),
            CancellationToken.None));

        Assert.Empty(await db.DocumentVersions.Where(item => item.ContentSha256 == new string('e', 64)).ToListAsync());
    }

    [Fact]
    public async Task Database_rejects_a_version_whose_document_belongs_to_another_knowledge_system()
    {
        await using var connection = await _fixture.OpenConnectionAsync();
        var otherKnowledgeSystemId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO knowledgesystem (id, "PublicId", "Name", "GraphIri", "BaseIri", "CreatedAt", "UpdatedAt")
                VALUES (@id, @public_id, 'Other', 'https://other.example/graph', 'https://other.example/base#', now(), now());
                """;
            command.Parameters.AddWithValue("id", otherKnowledgeSystemId);
            command.Parameters.AddWithValue("public_id", $"other-{Guid.NewGuid():N}");
            await command.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO document_version
                (id, knowledge_system_id, document_id, content_sha256, chunk_count, created_at)
            SELECT @id, @other_ks, id, @sha, 0, now()
            FROM document
            WHERE knowledge_system_id = @fixture_ks
            LIMIT 1;
            """;
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("other_ks", otherKnowledgeSystemId);
        insert.Parameters.AddWithValue("fixture_ks", _fixture.KnowledgeSystemId);
        insert.Parameters.AddWithValue("sha", new string('c', 64));

        await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Database_rejects_updates_and_deletes_of_versions_and_version_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var version = await new DocumentVersionStore(db).RecordAsync(
            new DocumentVersionInput(
                _fixture.KnowledgeSystemId,
                document.Id,
                new string('d', 64),
                [new DocumentVersionChunkInput(0, "immutable", 0, 9, 1)]),
            CancellationToken.None);
        var chunkId = await db.DocumentVersionChunks
            .Where(item => item.DocumentVersionId == version.Id)
            .Select(item => item.Id)
            .SingleAsync();

        await using var connection = await _fixture.OpenConnectionAsync();
        await using var updateVersion = connection.CreateCommand();
        updateVersion.CommandText = "UPDATE document_version SET chunk_count = 99 WHERE id = @id";
        updateVersion.Parameters.AddWithValue("id", version.Id);
        await Assert.ThrowsAsync<PostgresException>(() => updateVersion.ExecuteNonQueryAsync());

        await using var deleteVersion = connection.CreateCommand();
        deleteVersion.CommandText = "DELETE FROM document_version WHERE id = @id";
        deleteVersion.Parameters.AddWithValue("id", version.Id);
        await Assert.ThrowsAsync<PostgresException>(() => deleteVersion.ExecuteNonQueryAsync());

        await using var updateChunk = connection.CreateCommand();
        updateChunk.CommandText = "UPDATE document_version_chunk SET text = 'changed' WHERE id = @id";
        updateChunk.Parameters.AddWithValue("id", chunkId);
        await Assert.ThrowsAsync<PostgresException>(() => updateChunk.ExecuteNonQueryAsync());

        await using var deleteChunk = connection.CreateCommand();
        deleteChunk.CommandText = "DELETE FROM document_version_chunk WHERE id = @id";
        deleteChunk.Parameters.AddWithValue("id", chunkId);
        await Assert.ThrowsAsync<PostgresException>(() => deleteChunk.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Content_sha256_is_normalized_and_invalid_formats_are_rejected()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var store = new DocumentVersionStore(db);
        var uppercaseSha = new string('A', 64);
        var input = new DocumentVersionInput(
            _fixture.KnowledgeSystemId,
            document.Id,
            uppercaseSha,
            [new DocumentVersionChunkInput(0, "normalized", 0, 10, 1)]);

        var first = await store.RecordAsync(input, CancellationToken.None);
        var second = await store.RecordAsync(input with { ContentSha256 = uppercaseSha.ToLowerInvariant() }, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(new string('a', 64), first.ContentSha256);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(
            input with { ContentSha256 = "not-a-sha256" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_chunk_set_rolls_back_the_version_and_all_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var store = new DocumentVersionStore(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.RecordAsync(
            new DocumentVersionInput(
                _fixture.KnowledgeSystemId,
                document.Id,
                new string('f', 64),
                [
                    new DocumentVersionChunkInput(0, "first", 0, 5, 1),
                    new DocumentVersionChunkInput(0, "duplicate", 0, 9, 1),
                ]),
            CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.DocumentVersions.Where(item => item.ContentSha256 == new string('f', 64)).ToListAsync());
        Assert.Empty(await db.DocumentVersionChunks
            .Where(item => db.DocumentVersions
                .Where(version => version.ContentSha256 == new string('f', 64))
                .Select(version => version.Id)
                .Contains(item.DocumentVersionId))
            .ToListAsync());
    }

    [Fact]
    public async Task Migration_creates_the_version_schema_contract()
    {
        await using var connection = await _fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_name IN ('document_version', 'document_version_chunk')
            ORDER BY table_name, ordinal_position;
            """;

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
            }
        }

        Assert.Equal(
            [
                "document_version.id",
                "document_version.knowledge_system_id",
                "document_version.document_id",
                "document_version.content_sha256",
                "document_version.chunk_count",
                "document_version.created_at",
                "document_version_chunk.id",
                "document_version_chunk.document_version_id",
                "document_version_chunk.idx",
                "document_version_chunk.text",
                "document_version_chunk.char_start",
                "document_version_chunk.char_end",
                "document_version_chunk.token_estimate",
            ],
            columns);

        await using var constraintCommand = connection.CreateCommand();
        constraintCommand.CommandText = """
            SELECT indexname
            FROM pg_indexes
            WHERE tablename IN ('document_version', 'document_version_chunk')
              AND indexname IN (
                'ux_document_version_knowledge_system_document_sha256',
                'ux_document_version_chunk_version_idx');
            """;
        var indexes = new List<string>();
        await using (var reader = await constraintCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "ux_document_version_chunk_version_idx",
                "ux_document_version_knowledge_system_document_sha256",
            ],
            indexes.Order());

        await using var contractCommand = connection.CreateCommand();
        contractCommand.CommandText = """
            SELECT 'sha_length:' || character_maximum_length::text
            FROM information_schema.columns
            WHERE table_name = 'document_version' AND column_name = 'content_sha256'
            UNION ALL
            SELECT 'constraint:' || conname
            FROM pg_constraint
            WHERE conname IN (
                'ck_document_version_content_sha256_format')
            UNION ALL
            SELECT 'trigger:' || tgname
            FROM pg_trigger
            WHERE tgname IN ('document_version_immutable', 'document_version_chunk_immutable');
            """;
        var contract = new List<string>();
        await using (var reader = await contractCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                contract.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "constraint:ck_document_version_content_sha256_format",
                "sha_length:64",
                "trigger:document_version_chunk_immutable",
                "trigger:document_version_immutable",
            ],
            contract.Order());
    }
}