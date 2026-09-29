using System.Security.Cryptography;
using System.Text;
using ISEStudio.Application.Documents;
using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class DocumentIngestionJobProcessorTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public DocumentIngestionJobProcessorTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Document_parse_jobs_are_claimed_once_across_postgres_consumers()
    {
        await using var services = _fixture.BuildServices(configure: items =>
            items.AddSingleton<DocumentParseJobStore>());
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await AddDocumentAsync(db, "claim.txt");
        var version = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 1, Sha256 = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(version);
        var jobs = Enumerable.Range(0, 2).Select(_ => new DocumentParseJobEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            DocumentId = document.Id,
            DocumentFileVersionId = version.Id,
            Sha256 = version.Sha256,
            CreatedAt = DateTimeOffset.UtcNow,
        }).ToArray();
        db.DocumentParseJobs.AddRange(jobs);
        await db.SaveChangesAsync();

        var store = services.GetRequiredService<DocumentParseJobStore>();
        var claimed = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => store.ClaimNextAsync(CancellationToken.None)));
        Assert.Equal(2, claimed.Count(item => item is not null));
        Assert.Equal(2, claimed.Where(item => item is not null).Select(item => item!.Id).Distinct().Count());
        Assert.All(claimed.Where(item => item is not null), item => Assert.Equal("running", item!.Status));
    }

    [Fact]
    public async Task Document_parse_job_completes_and_links_the_pinned_version_on_postgres()
    {
        await using var services = _fixture.BuildServices(configure: items =>
        {
            items.AddSingleton(TimeProvider.System);
            items.AddScoped<DocumentParseJobProcessor>();
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "parse-job.txt");
        await PutBlobAsync(blobs, db, document, "pinned parse job content");
        var version = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 1, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(version);
        var job = new DocumentParseJobEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            DocumentId = document.Id,
            DocumentFileVersionId = version.Id,
            Sha256 = version.Sha256,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentParseJobs.Add(job);
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<DocumentParseJobProcessor>()
            .ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal("completed", (await db.DocumentParseJobs.AsNoTracking()
            .SingleAsync(item => item.Id == job.Id)).Status);
        Assert.Equal("parsed", (await db.Documents.AsNoTracking()
            .SingleAsync(item => item.Id == document.Id)).ParseStatus);
        var snapshot = await db.DocumentVersions.SingleAsync(item => item.DocumentId == document.Id);
        Assert.Equal(version.Id, (await db.DocumentFileVersionSnapshots.SingleAsync(
            item => item.DocumentVersionId == snapshot.Id)).DocumentFileVersionId);
        Assert.False(await db.ExtractionJobs.AnyAsync(item => item.Id == job.Id));
    }

    [Fact]
    public async Task Txt_blob_is_parsed_and_document_metadata_is_persisted()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var text = "甲😀乙\n\nSecond paragraph.";
        var document = await AddDocumentAsync(db, "parsed.txt");
        await PutBlobAsync(blobStore, db, document, text);

        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        var result = await scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None);

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("fallback:text", persisted.ParserBackend);
        Assert.Equal(text.EnumerateRunes().Count(), persisted.TextCharCount);
        Assert.Equal(result.Version.ChunkCount, persisted.ChunkCount);
        Assert.Equal(result.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == result.Version.Id));
    }

    [Fact]
    public async Task Missing_blob_fails_document_and_leaves_no_version_or_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var document = await AddDocumentAsync(db, "missing.txt");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("failed", persisted.ParseStatus);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ParseError));
        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal("failed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    [Fact]
    public async Task Replaced_cas_content_fails_before_creating_a_version()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = (LocalCasBlobStore)scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "replaced.txt");
        await PutBlobAsync(blobStore, db, document, "original content");

        var casPath = Path.Combine(blobStore.Root, document.StoragePath);
        await File.WriteAllTextAsync(casPath, "replacement content", Encoding.UTF8);

        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("failed", persisted.ParseStatus);
        Assert.Contains("SHA-256", persisted.ParseError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_seekable_blob_is_hashed_and_parsed_from_the_same_bytes()
    {
        await using var services = _fixture.BuildServices(configure: services =>
        {
            services.AddSingleton<IBlobStore>(_ => new NonSeekableBlobStore(
                Path.Combine(Path.GetTempPath(), "isestudio-non-seekable", Guid.NewGuid().ToString("N"))));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var text = "non-seekable blob content";
        var document = await AddDocumentAsync(db, "non-seekable.txt");
        await PutBlobAsync(blobStore, db, document, text);

        var result = await scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(
                new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test"),
                CancellationToken.None);

        Assert.Equal("completed", result.Status);
        var chunkText = await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == result.Version.Id)
            .OrderBy(item => item.Idx)
            .Select(item => item.Text)
            .ToListAsync();
        Assert.Equal(text, string.Join("\n", chunkText));
    }

    [Fact]
    public async Task Cancellation_after_version_persistence_finalizes_job_and_document_before_propagating()
    {
        using var cancellation = new CancellationTokenSource();
        await using var services = _fixture.BuildServices(configure: services =>
        {
            services.AddScoped<PlainTextIngestionService>(sp =>
                new CancellingIngestionService(
                    sp.GetRequiredService<DocumentVersionStore>(),
                    sp.GetRequiredService<Chunker>(),
                    cancellation));
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "cancelled.txt");
        await PutBlobAsync(blobStore, db, document, "cancel after version");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, cancellation.Token));

        var persistedJob = await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        var persistedDocument = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("failed", persistedJob.Status);
        Assert.Equal("failed", persistedDocument.ParseStatus);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
    }

    [Fact]
    public async Task Html_blob_is_parsed_and_document_metadata_is_persisted()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var html = "<html><body><h1>Durable HTML</h1><p>content</p></body></html>";
        var document = await AddDocumentAsync(db, "durable.html");
        await PutBlobAsync(blobStore, db, document, html);
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");

        var result = await scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None);

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("fallback:html", persisted.ParserBackend);
        Assert.Equal(result.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == result.Version.Id));
    }

    [Fact]
    public async Task Rdf_inputs_with_the_same_triple_count_keep_distinct_content()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var firstDocument = await AddDocumentAsync(db, "first.rdf");
        var secondDocument = await AddDocumentAsync(db, "second.rdf");
        var firstRdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:ex="https://example.test/">
              <rdf:Description rdf:about="https://example.test/subject">
                <ex:label xml:lang="en">Alpha</ex:label>
              </rdf:Description>
            </rdf:RDF>
            """;
        var secondRdf = firstRdf.Replace("Alpha", "Bravo", StringComparison.Ordinal);
        await PutBlobAsync(blobStore, db, firstDocument, firstRdf);
        await PutBlobAsync(blobStore, db, secondDocument, secondRdf);

        var processor = scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>();
        var first = await processor.ProcessAsync(
            new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId, firstDocument.Id, "stage-4-test"),
            CancellationToken.None);
        var second = await processor.ProcessAsync(
            new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId, secondDocument.Id, "stage-4-test"),
            CancellationToken.None);

        Assert.NotEqual(first.Version.ContentSha256, second.Version.ContentSha256);
        var firstText = string.Join("\n", await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == first.Version.Id)
            .OrderBy(item => item.Idx)
            .Select(item => item.Text)
            .ToListAsync());
        var secondText = string.Join("\n", await db.DocumentVersionChunks.AsNoTracking()
            .Where(item => item.DocumentVersionId == second.Version.Id)
            .OrderBy(item => item.Idx)
            .Select(item => item.Text)
            .ToListAsync());
        Assert.Contains("https://example.test/subject", firstText.Replace("\n", "", StringComparison.Ordinal));
        Assert.Contains("Alpha", firstText);
        Assert.Contains("Bravo", secondText);
        Assert.NotEqual(firstText, secondText);
    }

    [Theory]
    [InlineData("feed.rss", "<rss><channel><title>Durable feed</title><item><description>entry</description></item></channel></rss>", "application/rss+xml")]
    [InlineData("ontology.rdf", "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description><rdf:type>ClassRdf</rdf:type></rdf:Description></rdf:RDF>", "application/rdf+xml")]
    [InlineData("ontology.owl", "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description><rdf:type>ClassOwl</rdf:type></rdf:Description></rdf:RDF>", "application/rdf+xml")]
    public async Task Xml_document_format_is_persisted_through_the_same_version_contract(
        string filename,
        string content,
        string mediaType)
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, filename);
        await PutBlobAsync(blobStore, db, document, content);

        var result = await scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(
                new DocumentIngestionJob(
                    Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test"),
                CancellationToken.None);

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal(mediaType, persisted.Mime);
        Assert.Equal("document-parser/2", persisted.ParserVersion);
        Assert.True(result.Version.ChunkCount > 0);
    }

    [Fact]
    public async Task Duplicate_delivery_reuses_the_same_version_and_chunks()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "duplicate.txt");
        await PutBlobAsync(blobStore, db, document, "same content");
        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        var processor = scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>();

        var first = await processor.ProcessAsync(job, CancellationToken.None);
        var second = await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(first.Version.Id, second.Version.Id);
        Assert.Equal(1, await db.DocumentVersions.CountAsync(item => item.Id == first.Version.Id));
        Assert.Equal(first.Version.ChunkCount, await db.DocumentVersionChunks.CountAsync(
            item => item.DocumentVersionId == first.Version.Id));
    }

    [Fact]
    public async Task Delayed_job_parses_its_pinned_file_version_without_overwriting_current_document_state()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "delayed.txt");

        await PutBlobAsync(blobStore, db, document, "version A text");
        var first = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 1, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(first);
        await db.SaveChangesAsync();

        await PutBlobAsync(blobStore, db, document, "version B text");
        db.DocumentFileVersions.Add(new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 2, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await PutBlobAsync(blobStore, db, document, "version A text");
        var latest = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 3, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(latest);
        document.ParseStatus = "parsed";
        document.ParserBackend = "latest-version-parser";
        document.ChunkCount = 77;
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId,
                document.Id, "stage-4-test", first.Sha256, first.Id), CancellationToken.None);

        var link = await db.DocumentFileVersionSnapshots.SingleAsync(
            item => item.DocumentVersionId == result.Version.Id);
        Assert.Equal(first.Id, link.DocumentFileVersionId);
        Assert.NotEqual(latest.Id, link.DocumentFileVersionId);
        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("latest-version-parser", persisted.ParserBackend);
        Assert.Equal(77, persisted.ChunkCount);
    }

    [Fact]
    public async Task Delayed_job_parses_old_file_bytes_even_when_current_document_is_different()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "older-version.txt");
        await PutBlobAsync(blobStore, db, document, "version A");
        var versionA = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 1, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(versionA);
        await PutBlobAsync(blobStore, db, document, "version B");
        var versionB = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 2, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(versionB);
        await PutBlobAsync(blobStore, db, document, "version A");
        db.DocumentFileVersions.Add(new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 3, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        });
        document.ParseStatus = "parsed";
        document.ParserBackend = "current-parser";
        document.ChunkCount = 19;
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId,
                document.Id, "stage-4-test", versionB.Sha256, versionB.Id), CancellationToken.None);

        var link = await db.DocumentFileVersionSnapshots.SingleAsync(item => item.DocumentVersionId == result.Version.Id);
        Assert.Equal(versionB.Id, link.DocumentFileVersionId);
        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("current-parser", persisted.ParserBackend);
        Assert.Equal(19, persisted.ChunkCount);
    }

    [Fact]
    public async Task Missing_blob_for_old_file_version_does_not_fail_current_document()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var document = await AddDocumentAsync(db, "missing-old-version.txt");
        await PutBlobAsync(blobStore, db, document, "current version A");
        var missing = new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 1, Sha256 = new string('c', 64),
            SizeBytes = 4, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DocumentFileVersions.Add(missing);
        db.DocumentFileVersions.Add(new DocumentFileVersionEntity
        {
            DocumentId = document.Id, Version = 2, Sha256 = document.Sha256,
            SizeBytes = document.SizeBytes, CreatedAt = DateTimeOffset.UtcNow,
        });
        document.ParseStatus = "parsed";
        document.ParserBackend = "current-parser";
        document.ParseError = null;
        await db.SaveChangesAsync();
        var job = new DocumentIngestionJob(Guid.NewGuid(), _fixture.KnowledgeSystemId,
            document.Id, "stage-4-test", missing.Sha256, missing.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        var persisted = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id);
        Assert.Equal("parsed", persisted.ParseStatus);
        Assert.Equal("current-parser", persisted.ParserBackend);
        Assert.Null(persisted.ParseError);
        Assert.Equal("failed", (await db.ExtractionJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id)).Status);
    }

    [Fact]
    public async Task Cross_knowledge_system_document_is_rejected_without_writes()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var otherSystem = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Stage 4 other system",
            GraphIri = "https://example.test/stage-4-other",
            BaseIri = "https://example.test/stage-4-other#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = otherSystem.Id,
            Sha256 = new string('d', 64),
            OriginalFilename = "cross-system.txt",
            Ext = "txt",
            StoragePath = "dd/cross-system",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(otherSystem);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var job = new DocumentIngestionJob(
            Guid.NewGuid(), _fixture.KnowledgeSystemId, document.Id, "stage-4-test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<DocumentIngestionJobProcessor>()
            .ProcessAsync(job, CancellationToken.None));

        Assert.Equal(0, await db.DocumentVersions.CountAsync(item => item.DocumentId == document.Id));
        Assert.Equal("failed", (await db.ExtractionJobs.SingleAsync(item => item.Id == job.Id)).Status);
    }

    private async Task<DocumentEntity> AddDocumentAsync(ISEStudioDbContext db, string filename)
    {
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Sha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
            OriginalFilename = filename,
            Ext = Path.GetExtension(filename).TrimStart('.'),
            StoragePath = string.Empty,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task PutBlobAsync(
        IBlobStore blobStore,
        ISEStudioDbContext db,
        DocumentEntity document,
        string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var written = await blobStore.PutAsync(content, CancellationToken.None);
        document.Sha256 = written.Sha256;
        document.StoragePath = written.LegacyStoragePath;
        document.SizeBytes = Encoding.UTF8.GetByteCount(text);
        await db.SaveChangesAsync();
    }

    private sealed class NonSeekableBlobStore : IBlobStore
    {
        private readonly LocalCasBlobStore _inner;

        public NonSeekableBlobStore(string root) => _inner = new LocalCasBlobStore(root);

        public Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
            => _inner.PutAsync(content, cancellationToken);

        public async Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken)
        {
            var stream = await _inner.GetAsync(sha256, cancellationToken);
            return stream is null ? null : new NonSeekableReadStream(stream);
        }

        public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken)
            => _inner.ExistsAsync(sha256, cancellationToken);

        public Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
            => _inner.RemoveAsync(sha256, cancellationToken);
    }

    private sealed class CancellingIngestionService : PlainTextIngestionService
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingIngestionService(
            DocumentVersionStore versions,
            Chunker chunker,
            CancellationTokenSource cancellation)
            : base(versions, chunker)
        {
            _cancellation = cancellation;
        }

        public override async Task<DocumentVersionResult> IngestAsync(
            Guid knowledgeSystemId,
            Guid documentId,
            string content,
            CancellationToken cancellationToken,
            Guid? fileVersionId = null)
        {
            var result = await base.IngestAsync(
                knowledgeSystemId,
                documentId,
                content,
                CancellationToken.None,
                fileVersionId);
            _cancellation.Cancel();
            return result;
        }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableReadStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}