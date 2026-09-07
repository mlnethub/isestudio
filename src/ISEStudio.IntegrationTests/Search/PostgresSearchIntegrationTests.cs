using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Search;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Search;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Search;

public sealed class PostgresSearchIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private ISEStudioDbContext _db = null!;
    private PostgresSearchIndex _index = null!;
    private Guid _knowledgeSystemId;
    private Guid _otherKnowledgeSystemId;
    private Guid _ownerId;
    private Guid _grantedUserId;
    private Guid _currentVersionId;
    private Guid _historicalVersionId;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        _db = new ISEStudioDbContext(options);
        await _db.Database.MigrateAsync();
        _index = new PostgresSearchIndex(new TestContextFactory(options));
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Full_text_search_ranks_matches_and_supports_stable_pagination()
    {
        var firstPage = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            Limit: 1,
            ActorId: _ownerId));
        var secondPage = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            Limit: 1,
            Offset: 1,
            ActorId: _ownerId));

        Assert.Single(firstPage);
        Assert.Single(secondPage);
        Assert.NotEqual(firstPage[0].ChunkId, secondPage[0].ChunkId);
        Assert.True(firstPage[0].LexicalScore > 0);
    }

    [Fact]
    public async Task Tenant_and_permission_filters_prevent_cross_scope_reads()
    {
        var otherTenant = await _index.SearchAsync(new SearchRequest(
            _otherKnowledgeSystemId,
            "secret",
            ActorId: _grantedUserId));
        var crossTenant = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "secret",
            ActorId: _grantedUserId));
        var unauthorized = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            ActorId: Guid.NewGuid()));
        var granted = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            ActorId: _grantedUserId));

        Assert.Single(otherTenant);
        Assert.Empty(crossTenant);
        Assert.Empty(unauthorized);
        Assert.NotEmpty(granted);
        Assert.All(otherTenant, hit => Assert.Equal(new string('3', 64), hit.SourceSha256));
    }

    [Fact]
    public void Missing_actor_is_rejected_by_the_search_contract()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new SearchRequest(
            _knowledgeSystemId,
            "pump"));
    }

    [Fact]
    public async Task As_of_filter_returns_only_versions_visible_at_that_time()
    {
        var result = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            ActorId: _ownerId,
            AsOf: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));

        Assert.Single(result);
        Assert.Contains("calibration", result[0].Text, StringComparison.Ordinal);
        Assert.Equal(_historicalVersionId, result[0].DocumentVersionId);
    }

    [Fact]
    public async Task Search_without_as_of_returns_only_the_latest_version_per_document()
    {
        var result = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            ActorId: _ownerId));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, hit => hit.DocumentVersionId == _currentVersionId);
        Assert.DoesNotContain(result, hit => hit.DocumentVersionId == _historicalVersionId);
        Assert.Equal(result.Count, result.Select(hit => hit.DocumentId).Distinct().Count());
    }

    [Fact]
    public async Task Search_returns_empty_for_a_query_without_matches()
    {
        var result = await _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "no-such-search-term",
            ActorId: _ownerId));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Vector_request_reports_capability_gap_instead_of_faking_vector_results()
    {
        Assert.False(_index.Capabilities.SupportsVectorSearch);

        await Assert.ThrowsAsync<NotSupportedException>(() => _index.SearchAsync(new SearchRequest(
            _knowledgeSystemId,
            "pump",
            QueryVector: new[] { 0.1f, 0.2f },
            ActorId: _ownerId)));
    }

    private async Task SeedAsync()
    {
        _ownerId = Guid.NewGuid();
        _grantedUserId = Guid.NewGuid();
        _knowledgeSystemId = Guid.NewGuid();
        _otherKnowledgeSystemId = Guid.NewGuid();

        _db.Users.AddRange(
            new UserEntity
            {
                Id = _ownerId,
                Username = "search-owner",
                PasswordHash = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new UserEntity
            {
                Id = _grantedUserId,
                Username = "search-granted",
                PasswordHash = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        _db.KnowledgeSystems.AddRange(
            new KnowledgeSystemEntity
            {
                Id = _knowledgeSystemId,
                PublicId = "search-ks",
                Name = "Search KS",
                OwnerId = _ownerId,
                GraphIri = "urn:search:ks",
                BaseIri = "urn:search:ks#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new KnowledgeSystemEntity
            {
                Id = _otherKnowledgeSystemId,
                PublicId = "other-search-ks",
                Name = "Other Search KS",
                OwnerId = _grantedUserId,
                GraphIri = "urn:search:other",
                BaseIri = "urn:search:other#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        _db.KSGrants.Add(new KSGrantEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            UserId = _grantedUserId,
            Role = "viewer",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var currentDocument = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            Sha256 = new string('1', 64),
            OriginalFilename = "current.txt",
            Ext = "txt",
            StoragePath = "11/current",
            UploadedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ParseStatus = "parsed",
        };
        var historicalDocument = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            Sha256 = new string('2', 64),
            OriginalFilename = "historical.txt",
            Ext = "txt",
            StoragePath = "22/historical",
            UploadedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ParseStatus = "parsed",
        };
        var otherDocument = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _otherKnowledgeSystemId,
            Sha256 = new string('3', 64),
            OriginalFilename = "other.txt",
            Ext = "txt",
            StoragePath = "33/other",
            UploadedAt = DateTimeOffset.UtcNow,
            ParseStatus = "parsed",
        };
        _db.Documents.AddRange(currentDocument, historicalDocument, otherDocument);

        var currentVersion = new DocumentVersionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            DocumentId = currentDocument.Id,
            ContentSha256 = new string('4', 64),
            CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ChunkCount = 1,
        };
        _currentVersionId = currentVersion.Id;
        var historicalVersion = new DocumentVersionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            DocumentId = currentDocument.Id,
            ContentSha256 = new string('5', 64),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ChunkCount = 1,
        };
        _historicalVersionId = historicalVersion.Id;
        var archiveVersion = new DocumentVersionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _knowledgeSystemId,
            DocumentId = historicalDocument.Id,
            ContentSha256 = new string('7', 64),
            CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ChunkCount = 1,
        };
        var otherVersion = new DocumentVersionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _otherKnowledgeSystemId,
            DocumentId = otherDocument.Id,
            ContentSha256 = new string('6', 64),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ChunkCount = 1,
        };
        _db.DocumentVersions.AddRange(currentVersion, historicalVersion, archiveVersion, otherVersion);
        _db.DocumentVersionChunks.AddRange(
            new DocumentVersionChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = currentVersion.Id,
                Idx = 0,
                Text = "pump pressure procedure",
            },
            new DocumentVersionChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = historicalVersion.Id,
                Idx = 0,
                Text = "pump calibration procedure",
            },
            new DocumentVersionChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = archiveVersion.Id,
                Idx = 0,
                Text = "pump valve procedure",
            },
            new DocumentVersionChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentVersionId = otherVersion.Id,
                Idx = 0,
                Text = "secret pump procedure",
            });

        await _db.SaveChangesAsync();
    }

    private sealed class TestContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;

        public TestContextFactory(DbContextOptions<ISEStudioDbContext> options)
        {
            _options = options;
        }

        public ISEStudioDbContext CreateDbContext() => new(_options);

        public Task<ISEStudioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ISEStudioDbContext(_options));
    }
}