using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class PlainTextIngestionServiceTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public PlainTextIngestionServiceTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Plain_text_is_hashed_chunked_and_persisted_with_metadata()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var text = "First paragraph.\n\nSecond paragraph.";

        var result = await new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 20, overlap: 0))
            .IngestAsync(_fixture.KnowledgeSystemId, document.Id, text, CancellationToken.None);

        var expectedSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
        Assert.Equal(expectedSha, result.ContentSha256);
        Assert.Equal(2, result.ChunkCount);

        var chunks = await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == result.Id)
            .OrderBy(item => item.Idx)
            .ToListAsync();
        Assert.Collection(
            chunks,
            first =>
            {
                Assert.Equal(0, first.Idx);
                Assert.Equal("First paragraph.", first.Text);
                Assert.Equal(0, first.CharStart);
                Assert.Equal(16, first.CharEnd);
                Assert.Equal(6, first.TokenEstimate);
            },
            second =>
            {
                Assert.Equal(1, second.Idx);
                Assert.Equal("Second paragraph.", second.Text);
                Assert.Equal(18, second.CharStart);
                Assert.Equal(35, second.CharEnd);
                Assert.Equal(6, second.TokenEstimate);
            });
    }

    [Fact]
    public async Task Unicode_scalar_offsets_and_token_estimates_are_persisted()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var text = "甲😀乙😀丙😀丁";

        var result = await new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 3, overlap: 0))
            .IngestAsync(_fixture.KnowledgeSystemId, document.Id, text, CancellationToken.None);

        var chunks = await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == result.Id)
            .OrderBy(item => item.Idx)
            .ToListAsync();

        Assert.Collection(
            chunks,
            first =>
            {
                Assert.Equal("甲😀乙", first.Text);
                Assert.Equal(0, first.CharStart);
                Assert.Equal(3, first.CharEnd);
                Assert.Equal(3, first.TokenEstimate);
            },
            second =>
            {
                Assert.Equal("😀丙😀", second.Text);
                Assert.Equal(3, second.CharStart);
                Assert.Equal(6, second.CharEnd);
                Assert.Equal(3, second.TokenEstimate);
            },
            third =>
            {
                Assert.Equal("丁", third.Text);
                Assert.Equal(6, third.CharStart);
                Assert.Equal(7, third.CharEnd);
                Assert.Equal(1, third.TokenEstimate);
            });
    }

    [Fact]
    public async Task Repeating_text_is_idempotent_and_changed_text_creates_a_new_version()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await db.Documents.SingleAsync(
            item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        var service = new PlainTextIngestionService(
            new DocumentVersionStore(db),
            new Parsing.Chunker(size: 20, overlap: 0));

        var first = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "same content", CancellationToken.None);
        var repeat = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "same content", CancellationToken.None);
        var changed = await service.IngestAsync(
            _fixture.KnowledgeSystemId, document.Id, "changed content", CancellationToken.None);

        Assert.Equal(first.Id, repeat.Id);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(
            item => item.Id == first.Id));
        Assert.Equal(1, await db.DocumentVersions.CountAsync(
            item => item.Id == changed.Id));
        Assert.Equal(1, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Id));
        Assert.Equal(1, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == changed.Id));
    }
}