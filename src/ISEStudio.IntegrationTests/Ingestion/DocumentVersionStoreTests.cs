using ISEStudio.Documents;
using ISEStudio.Application.Documents;
using ISEStudio.Infrastructure.Persistence;
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
            "version-sha-1",
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
                "cross-tenant-sha",
                [new DocumentVersionChunkInput(0, "secret", 0, 6, 1)]),
            CancellationToken.None));

        Assert.Empty(await db.DocumentVersions.Where(item => item.ContentSha256 == "cross-tenant-sha").ToListAsync());
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
                "rollback-sha",
                [
                    new DocumentVersionChunkInput(0, "first", 0, 5, 1),
                    new DocumentVersionChunkInput(0, "duplicate", 0, 9, 1),
                ]),
            CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.DocumentVersions.Where(item => item.ContentSha256 == "rollback-sha").ToListAsync());
        Assert.Empty(await db.DocumentVersionChunks
            .Where(item => db.DocumentVersions
                .Where(version => version.ContentSha256 == "rollback-sha")
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
    }
}