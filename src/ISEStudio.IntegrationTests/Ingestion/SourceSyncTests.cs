using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ISEStudio.Application.Foundation;
using ISEStudio.Authorization;
using ISEStudio.Documents;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Sources;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISEStudio.IntegrationTests.Ingestion;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SourceSyncTestCollection
{
    public const string Name = "Source sync integration tests";
}

[Collection(SourceSyncTestCollection.Name)]
public sealed class SourceSyncTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;
    private readonly string _contentNamespace = Guid.NewGuid().ToString("N");

    public SourceSyncTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Source_external_key_binding_is_unique_per_source()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-binding-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var document = await db.Documents.Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .OrderBy(item => item.Id).FirstAsync();
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        db.SourceDocumentBindings.Add(new SourceDocumentBindingEntity
        {
            SourceId = source.Id,
            ExternalKey = "remote-key-a",
            DocumentId = document.Id,
        });
        await db.SaveChangesAsync();

        db.SourceDocumentBindings.Add(new SourceDocumentBindingEntity
        {
            SourceId = source.Id,
            ExternalKey = "remote-key-a",
            DocumentId = document.Id,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Sync_retries_same_bytes_idempotently_and_pins_parse_job_to_new_version()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-upsert-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        var adapter = new SequenceSourceAdapter(
            Scan("alpha"), Scan("alpha"), Scan("bravo"));
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var coordinator = new SourceSyncCoordinator(contexts, blobs, [adapter], jobs, TimeProvider.System);

        async Task<SourceSyncResult> RunScanAsync()
        {
            var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
            var claimed = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            var runId = Assert.IsType<Guid>(claimed.ActiveRunId);
            var result = await coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
            return result;
        }

        var firstResult = await RunScanAsync();
        Assert.Equal(1, firstResult.Added);
        Assert.Equal(0, firstResult.Updated);
        var firstState = await ReadSourceStateAsync(contexts, source.Id);
        var firstDocument = Assert.Single(firstState.Documents);
        var firstFileVersion = Assert.Single(firstState.FileVersions);
        Assert.Equal(1, firstFileVersion.Version);
        Assert.Equal(Hash("alpha"), firstDocument.Sha256);
        Assert.Single(firstState.Bindings);
        Assert.Single(firstState.ParseJobs);

        var retryResult = await RunScanAsync();
        Assert.Equal(0, retryResult.Added);
        Assert.Equal(0, retryResult.Updated);
        var retryState = await ReadSourceStateAsync(contexts, source.Id);
        Assert.Single(retryState.Documents);
        Assert.Single(retryState.FileVersions);
        Assert.Single(retryState.ParseJobs);

        var changedResult = await RunScanAsync();
        Assert.Equal(0, changedResult.Added);
        Assert.Equal(1, changedResult.Updated);
        var changedState = await ReadSourceStateAsync(contexts, source.Id);
        var changedDocument = Assert.Single(changedState.Documents);
        Assert.Equal(firstDocument.Id, changedDocument.Id);
        Assert.Equal(Hash("bravo"), changedDocument.Sha256);
        Assert.Equal([1, 2], changedState.FileVersions.OrderBy(item => item.Version)
            .Select(item => item.Version).ToArray());
        Assert.Equal(2, changedState.ParseJobs.Count);
        var latestVersion = changedState.FileVersions.Single(item => item.Version == 2);
        var latestParseJob = changedState.ParseJobs.Single(item => item.DocumentFileVersionId == latestVersion.Id);
        Assert.Equal(latestVersion.Sha256, latestParseJob.Sha256);
    }

    [Fact]
    public async Task Complete_scan_marks_absent_binding_and_reappearance_survives_later_scan_failure()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-reconcile-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var adapter = new SequenceSourceAdapter(
            ScanItems(Item("key-a", "alpha"), Item("key-b", "beta")),
            ScanItems(),
            new SourceScan(EnumerateThenThrow(Item("key-a", "alpha")), IsComplete: true));
        var coordinator = new SourceSyncCoordinator(contexts,
            scope.ServiceProvider.GetRequiredService<IBlobStore>(), [adapter], jobs, TimeProvider.System);

        async Task<SourceSyncResult> RunScanAsync()
        {
            var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(source.Id, claim.SourceId);
            var runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
            return result;
        }

        await RunScanAsync();
        await RunScanAsync();
        var afterCompleteScan = await ReadSourceStateAsync(contexts, source.Id);
        var missingA = Assert.Single(afterCompleteScan.Bindings, binding => binding.ExternalKey == "key-a");
        var missingB = Assert.Single(afterCompleteScan.Bindings, binding => binding.ExternalKey == "key-b");
        Assert.NotNull(missingA.MissingSince);
        Assert.NotNull(missingB.MissingSince);
        var documentAId = afterCompleteScan.FileVersions.Single(version => version.Sha256 == Hash("alpha")).DocumentId;
        Assert.NotNull(afterCompleteScan.Documents.Single(document => document.Id == documentAId).MissingSince);

        var partialResult = await RunScanAsync();
        Assert.False(partialResult.IsComplete);
        Assert.NotEmpty(partialResult.Errors);
        var afterPartialScan = await ReadSourceStateAsync(contexts, source.Id);
        Assert.Null(Assert.Single(afterPartialScan.Bindings, binding => binding.ExternalKey == "key-a").MissingSince);
        Assert.NotNull(Assert.Single(afterPartialScan.Bindings, binding => binding.ExternalKey == "key-b").MissingSince);
        Assert.Null(afterPartialScan.Documents.Single(document => document.Id == documentAId).MissingSince);
        var documentBId = afterPartialScan.FileVersions.Single(version => version.Sha256 == Hash("beta")).DocumentId;
        Assert.NotNull(afterPartialScan.Documents.Single(document => document.Id == documentBId).MissingSince);
    }

    [Fact]
    public async Task Shared_document_is_missing_only_after_all_bindings_are_missing()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-shared-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var adapter = new SequenceSourceAdapter(
            ScanItems(Item("key-a", "shared"), Item("key-b", "shared")),
            ScanItems(Item("key-a", "shared")),
            ScanItems());
        var coordinator = new SourceSyncCoordinator(contexts,
            scope.ServiceProvider.GetRequiredService<IBlobStore>(), [adapter], jobs, TimeProvider.System);

        async Task RunScanAsync()
        {
            var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            var runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
        }

        await RunScanAsync();
        var initial = await ReadSourceStateAsync(contexts, source.Id);
        Assert.Equal(2, initial.Bindings.Count);
        var sharedDocument = Assert.Single(initial.Documents);
        Assert.All(initial.Bindings, binding => Assert.Equal(sharedDocument.Id, binding.DocumentId));

        await RunScanAsync();
        var oneActive = await ReadSourceStateAsync(contexts, source.Id);
        Assert.Null(oneActive.Documents.Single().MissingSince);
        Assert.NotNull(Assert.Single(oneActive.Bindings, binding => binding.ExternalKey == "key-b").MissingSince);

        await RunScanAsync();
        var allMissing = await ReadSourceStateAsync(contexts, source.Id);
        Assert.All(allMissing.Bindings, binding => Assert.NotNull(binding.MissingSince));
        Assert.NotNull(Assert.Single(allMissing.Documents).MissingSince);
    }

    [Fact]
    public async Task Same_sha_across_sources_shares_document_and_reconciles_missing_across_sources()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var sourceA = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-cross-source-a-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var sourceB = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-cross-source-b-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.AddRange(sourceA, sourceB);
        await db.SaveChangesAsync();

        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var adapterA = new SequenceSourceAdapter(Scan("identical"), ScanItems());
        var adapterB = new SequenceSourceAdapter(Scan("identical"), ScanItems());
        var coordinatorA = new SourceSyncCoordinator(contexts,
            scope.ServiceProvider.GetRequiredService<IBlobStore>(), [adapterA], jobs, TimeProvider.System);
        var coordinatorB = new SourceSyncCoordinator(contexts,
            scope.ServiceProvider.GetRequiredService<IBlobStore>(), [adapterB], jobs, TimeProvider.System);

        async Task RunScanAsync(Guid sourceId, SourceSyncCoordinator coordinator)
        {
            var jobId = await jobs.EnqueueAsync(sourceId, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            var runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(sourceId, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
        }

        await RunScanAsync(sourceA.Id, coordinatorA);
        await RunScanAsync(sourceB.Id, coordinatorB);
        var bindings = await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.SourceId == sourceA.Id || binding.SourceId == sourceB.Id)
            .ToListAsync();
        Assert.Equal(2, bindings.Count);
        Assert.Equal(bindings[0].DocumentId, bindings[1].DocumentId);
        var sharedDocument = await db.Documents.SingleAsync(document => document.Id == bindings[0].DocumentId);

        await RunScanAsync(sourceA.Id, coordinatorA);
        Assert.Null(sharedDocument.MissingSince);

        await RunScanAsync(sourceB.Id, coordinatorB);
        db.ChangeTracker.Clear();
        sharedDocument = await db.Documents.AsNoTracking().SingleAsync(document => document.Id == sharedDocument.Id);
        Assert.NotNull(sharedDocument.MissingSince);
    }

    [Fact]
    public async Task Changed_content_forks_a_document_shared_with_another_source()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var sourceA = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-shared-owner-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var sourceB = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-shared-fork-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.AddRange(sourceA, sourceB);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var adapterA = new SequenceSourceAdapter(Scan("alpha"));
        var adapterB = new SequenceSourceAdapter(Scan("alpha"), Scan("bravo"));
        var coordinatorA = new SourceSyncCoordinator(contexts, blobs, [adapterA], jobs, TimeProvider.System);
        var coordinatorB = new SourceSyncCoordinator(contexts, blobs, [adapterB], jobs, TimeProvider.System);

        async Task RunScanAsync(Guid sourceId, SourceSyncCoordinator coordinator)
        {
            var jobId = await jobs.EnqueueAsync(sourceId, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(sourceId, claim.SourceId);
            var runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(sourceId, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
            Assert.True(result.IsComplete);
            Assert.Empty(result.Errors);
        }

        await RunScanAsync(sourceA.Id, coordinatorA);
        await RunScanAsync(sourceB.Id, coordinatorB);
        var beforeChange = await ReadSourceStateAsync(contexts, sourceA.Id);
        var originalDocument = Assert.Single(beforeChange.Documents);
        Assert.Equal(1, beforeChange.FileVersions.Count(version => version.DocumentId == originalDocument.Id));

        await RunScanAsync(sourceB.Id, coordinatorB);
        var afterChange = await ReadSourceStateAsync(contexts, sourceA.Id);
        var sourceADocument = Assert.Single(afterChange.Documents);
        Assert.Equal(originalDocument.Id, sourceADocument.Id);
        Assert.Equal(Hash("alpha"), sourceADocument.Sha256);
        Assert.Equal(sourceA.Id, sourceADocument.SourceId);
        Assert.Single(afterChange.Bindings);
        Assert.Equal(originalDocument.Id, Assert.Single(afterChange.Bindings).DocumentId);

        var sourceBState = await ReadSourceStateAsync(contexts, sourceB.Id);
        var forkedDocument = Assert.Single(sourceBState.Documents, document => document.Id != originalDocument.Id);
        Assert.Equal(Hash("bravo"), forkedDocument.Sha256);
        Assert.Equal([1], sourceBState.FileVersions.Where(version => version.DocumentId == forkedDocument.Id)
            .Select(version => version.Version).ToArray());
        Assert.Single(sourceBState.ParseJobs, job => job.DocumentId == forkedDocument.Id);
        Assert.Equal("remote-key-a", Assert.Single(sourceBState.Bindings).ExternalKey);
    }

    [Fact]
    public async Task Concurrent_cross_source_reconciliation_preserves_aggregate_missing_state()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var sourceA = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-concurrent-a-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var sourceB = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-concurrent-b-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var sharedDocument = CreateDocument(sourceA.Id, "shared-content", "shared.txt");
        db.Sources.AddRange(sourceA, sourceB);
        db.Documents.Add(sharedDocument);
        await db.SaveChangesAsync();
        db.SourceDocumentBindings.AddRange(
            new SourceDocumentBindingEntity
            {
                SourceId = sourceA.Id,
                ExternalKey = "remote-key-a",
                DocumentId = sharedDocument.Id,
            },
            new SourceDocumentBindingEntity
            {
                SourceId = sourceB.Id,
                ExternalKey = "remote-key-a",
                DocumentId = sharedDocument.Id,
            });
        await db.SaveChangesAsync();

        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        async Task<(Guid JobId, Guid RunId)> ClaimAsync(Guid sourceId)
        {
            var jobId = await jobs.EnqueueAsync(sourceId, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(sourceId, claim.SourceId);
            return (jobId, Assert.IsType<Guid>(claim.ActiveRunId));
        }

        async Task RunConcurrentlyAsync(SourceScan scanA, SourceScan scanB)
        {
            var (jobA, runA) = await ClaimAsync(sourceA.Id);
            var (jobB, runB) = await ClaimAsync(sourceB.Id);
            var gate = new TwoPartyScanGate();
            var coordinatorA = new SourceSyncCoordinator(contexts, blobs,
                [new GatedSourceAdapter(scanA, gate)], jobs, TimeProvider.System);
            var coordinatorB = new SourceSyncCoordinator(contexts, blobs,
                [new GatedSourceAdapter(scanB, gate)], jobs, TimeProvider.System);
            var results = await Task.WhenAll(
                coordinatorA.RunAsync(sourceA.Id, jobA, runA, CancellationToken.None),
                coordinatorB.RunAsync(sourceB.Id, jobB, runB, CancellationToken.None));
            Assert.All(results, result =>
            {
                Assert.True(result.IsComplete);
                Assert.Empty(result.Errors);
            });
            await jobs.CompleteAsync(jobA, runA, results[0], CancellationToken.None);
            await jobs.CompleteAsync(jobB, runB, results[1], CancellationToken.None);
        }

        await RunConcurrentlyAsync(ScanItems(), ScanItems());
        db.ChangeTracker.Clear();
        var persisted = await db.Documents.AsNoTracking().SingleAsync(document => document.Id == sharedDocument.Id);
        Assert.NotNull(persisted.MissingSince);

        await RunConcurrentlyAsync(ScanItems(), Scan("shared-content"));
        db.ChangeTracker.Clear();
        persisted = await db.Documents.AsNoTracking().SingleAsync(document => document.Id == sharedDocument.Id);
        Assert.Null(persisted.MissingSince);
        Assert.NotNull(await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.SourceId == sourceA.Id).Select(binding => binding.MissingSince).SingleAsync());
        Assert.Null(await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.SourceId == sourceB.Id).Select(binding => binding.MissingSince).SingleAsync());
    }

    [Fact]
    public async Task Expired_run_cannot_upsert_or_reconcile_after_a_new_run_is_claimed()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-stale-run-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var existingDocument = CreateDocument(source.Id, "existing-content", "existing.txt");
        db.Sources.Add(source);
        db.Documents.Add(existingDocument);
        await db.SaveChangesAsync();
        var existingBinding = new SourceDocumentBindingEntity
        {
            SourceId = source.Id,
            ExternalKey = "existing-key",
            DocumentId = existingDocument.Id,
        };
        db.SourceDocumentBindings.Add(existingBinding);
        await db.SaveChangesAsync();

        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var jobs = new SourceSyncJobStore(contexts, clock, TimeSpan.FromSeconds(30));
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var oldClaim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        var oldRunId = Assert.IsType<Guid>(oldClaim.ActiveRunId);
        clock.Advance(TimeSpan.FromSeconds(31));
        var newClaim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        var newRunId = Assert.IsType<Guid>(newClaim.ActiveRunId);
        Assert.NotEqual(oldRunId, newRunId);

        var staleCoordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(Item("stale-key", "stale-content")))], jobs, clock);
        var staleResult = await staleCoordinator.RunAsync(source.Id, jobId, oldRunId, CancellationToken.None);

        Assert.True(staleResult.IsComplete);
        Assert.NotEmpty(staleResult.Errors);
        db.ChangeTracker.Clear();
        Assert.False(await db.SourceDocumentBindings.AnyAsync(binding =>
            binding.SourceId == source.Id && binding.ExternalKey == "stale-key"));
        Assert.Null(await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.Id == existingBinding.Id)
            .Select(binding => binding.MissingSince).SingleAsync());
        Assert.False(await db.Documents.AnyAsync(document =>
            document.KnowledgeSystemId == _fixture.KnowledgeSystemId
            && document.Sha256 == Hash("stale-content")));

        var currentCoordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(Item("existing-key", "existing-content")))], jobs, clock);
        var currentResult = await currentCoordinator.RunAsync(source.Id, jobId, newRunId, CancellationToken.None);
        Assert.True(currentResult.IsComplete);
        Assert.Empty(currentResult.Errors);
        await jobs.CompleteAsync(jobId, newRunId, currentResult, CancellationToken.None);
    }

    [Fact]
    public async Task Concurrent_duplicate_item_upserts_create_one_binding_and_file_version()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-concurrent-key-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        var runId = Assert.IsType<Guid>(claim.ActiveRunId);
        var gate = new TwoPartyScanGate();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var coordinatorA = new SourceSyncCoordinator(contexts, blobs,
            [new GatedSourceAdapter(Scan("same-content"), gate)], jobs, TimeProvider.System);
        var coordinatorB = new SourceSyncCoordinator(contexts, blobs,
            [new GatedSourceAdapter(Scan("same-content"), gate)], jobs, TimeProvider.System);

        var results = await Task.WhenAll(
            coordinatorA.RunAsync(source.Id, jobId, runId, CancellationToken.None),
            coordinatorB.RunAsync(source.Id, jobId, runId, CancellationToken.None));

        Assert.All(results, result =>
        {
            Assert.True(result.IsComplete);
            Assert.Empty(result.Errors);
        });
        Assert.Equal(1, results.Sum(result => result.Added));
        var state = await ReadSourceStateAsync(contexts, source.Id);
        var document = Assert.Single(state.Documents);
        Assert.Single(state.Bindings);
        Assert.Equal(source.Id, document.SourceId);
        Assert.Single(state.FileVersions);
        Assert.Single(state.ParseJobs);
        await jobs.CompleteAsync(jobId, runId,
            new SourceSyncResult(results.Sum(result => result.Added), results.Sum(result => result.Updated), [], true),
            CancellationToken.None);
    }

    [Fact]
    public async Task Failed_blob_cleanup_waits_for_same_sha_ingestion_and_preserves_its_blob()
    {
        var blobRoot = Path.Combine(Path.GetTempPath(), "isestudio-source-sync-blob-race", Guid.NewGuid().ToString("N"));
        var blobs = new PausingFirstRemovalBlobStore(new LocalCasBlobStore(blobRoot));
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var sourceA = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-blob-fail-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var sourceB = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-blob-success-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.AddRange(sourceA, sourceB);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);

        async Task<(Guid JobId, Guid RunId)> ClaimAsync(Guid sourceId)
        {
            var jobId = await jobs.EnqueueAsync(sourceId, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(sourceId, claim.SourceId);
            return (jobId, Assert.IsType<Guid>(claim.ActiveRunId));
        }

        var (jobA, runA) = await ClaimAsync(sourceA.Id);
        var (jobB, runB) = await ClaimAsync(sourceB.Id);
        var content = Encoding.UTF8.GetBytes(Content("same-sha-pending-cleanup"));
        var invalidFilename = new string('x', 1100) + ".txt";
        var invalidItem = new SourceItem("failed-key", invalidFilename, "text/plain",
            new MemoryStream(content), DateTimeOffset.UtcNow);
        var validItem = new SourceItem("successful-key", "valid.txt", "text/plain",
            new MemoryStream(content), DateTimeOffset.UtcNow);
        var failedCoordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(invalidItem))], jobs, TimeProvider.System);
        var successfulCoordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(validItem))], jobs, TimeProvider.System);
        var failedTask = failedCoordinator.RunAsync(sourceA.Id, jobA, runA, CancellationToken.None);
        await blobs.RemoveStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var successfulTask = successfulCoordinator.RunAsync(sourceB.Id, jobB, runB, CancellationToken.None);

        var completedBeforeCleanup = await Task.WhenAny(successfulTask, Task.Delay(TimeSpan.FromMilliseconds(250)))
            == successfulTask;
        var putsBeforeCleanup = blobs.PutCallCount;
        blobs.AllowRemove();
        var failedResult = await failedTask.WaitAsync(TimeSpan.FromSeconds(10));
        var successfulResult = await successfulTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(failedResult.IsComplete);
        Assert.NotEmpty(failedResult.Errors);
        Assert.False(completedBeforeCleanup);
        Assert.Equal(1, putsBeforeCleanup);
        Assert.Equal(2, blobs.PutCallCount);
        Assert.Equal(1, successfulResult.Added);
        Assert.Empty(successfulResult.Errors);
        await jobs.CompleteAsync(jobA, runA, failedResult, CancellationToken.None);
        await jobs.CompleteAsync(jobB, runB, successfulResult, CancellationToken.None);

        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.True(await blobs.ExistsAsync(sha, CancellationToken.None));
        Assert.False(await db.SourceDocumentBindings.AnyAsync(binding => binding.SourceId == sourceA.Id));
        var successfulBinding = await db.SourceDocumentBindings.SingleAsync(binding => binding.SourceId == sourceB.Id);
        var persistedDocument = await db.Documents.SingleAsync(document => document.Id == successfulBinding.DocumentId);
        Assert.Equal(sha, persistedDocument.Sha256);
        Assert.Single(await db.DocumentFileVersions.Where(version => version.DocumentId == persistedDocument.Id)
            .ToListAsync());

        if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);
    }

    [Fact]
    public async Task Source_delete_waits_for_inflight_item_then_rejects_active_sync()
    {
        var blobRoot = Path.Combine(Path.GetTempPath(), "isestudio-source-sync-source-delete", Guid.NewGuid().ToString("N"));
        var innerBlobs = new LocalCasBlobStore(blobRoot);
        var blobs = new PausingFirstPutBlobStore(innerBlobs);
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var (source, actor) = await CreateSourceAndActorAsync(db);
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(source.Id, claim.SourceId);
        var runId = Assert.IsType<Guid>(claim.ActiveRunId);
        var coordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(Item("active-key", "active-content")))],
            jobs, TimeProvider.System);
        var syncTask = coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
        await blobs.PutStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var deleteTask = DeleteSourceAsync(source.Id, actor.Id);
        var deleteFinishedWhilePutPaused = await Task.WhenAny(
            deleteTask, Task.Delay(TimeSpan.FromMilliseconds(250))) == deleteTask;
        blobs.AllowPut();
        var syncResult = await syncTask.WaitAsync(TimeSpan.FromSeconds(10));
        var deleteStatus = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(deleteFinishedWhilePutPaused);
        Assert.True(syncResult.IsComplete);
        Assert.Empty(syncResult.Errors);
        Assert.Equal(1, syncResult.Added);
        Assert.Equal(409, deleteStatus);
        await jobs.CompleteAsync(jobId, runId, syncResult, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.True(await db.Sources.AnyAsync(item => item.Id == source.Id));
        var binding = await db.SourceDocumentBindings.SingleAsync(item =>
            item.SourceId == source.Id && item.ExternalKey == "active-key");
        var document = await db.Documents.SingleAsync(item => item.Id == binding.DocumentId);
        Assert.Equal(Hash("active-content"), document.Sha256);
        Assert.True(await innerBlobs.ExistsAsync(document.Sha256, CancellationToken.None));
        if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);
    }

    [Fact]
    public async Task Source_delete_queued_before_item_write_rejects_then_allows_sync()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var (source, actor) = await CreateSourceAndActorAsync(db);
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        var runId = Assert.IsType<Guid>(claim.ActiveRunId);
        var coordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(Item("delete-first-key", "delete-first-content")))],
            jobs, TimeProvider.System);

        await using var blockerConnection = await _fixture.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var lockCommand = blockerConnection.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText = "SELECT id FROM source WHERE id = @source_id FOR UPDATE";
            lockCommand.Parameters.AddWithValue("source_id", source.Id);
            await lockCommand.ExecuteScalarAsync();
        }
        int blockerPid;
        await using (var pidCommand = blockerConnection.CreateCommand())
        {
            pidCommand.Transaction = blockerTransaction;
            pidCommand.CommandText = "SELECT pg_backend_pid()";
            blockerPid = (int)(await pidCommand.ExecuteScalarAsync())!;
        }

        var deleteTask = DeleteSourceAsync(source.Id, actor.Id);
        var deletePid = await WaitForBlockedSourceCommandAsync(isDelete: true, blockerPid);
        var syncTask = coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
        var syncPid = await WaitForBlockedSourceCommandAsync(isDelete: false, deletePid);

        await blockerTransaction.CommitAsync();
        var deleteStatus = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
        var syncResult = await syncTask.WaitAsync(TimeSpan.FromSeconds(10));
        await jobs.CompleteAsync(jobId, runId, syncResult, CancellationToken.None);

        Assert.Equal(409, deleteStatus);
        Assert.True(syncResult.IsComplete);
        Assert.Empty(syncResult.Errors);
        Assert.Equal(1, syncResult.Added);
        db.ChangeTracker.Clear();
        Assert.True(await db.Sources.AnyAsync(item => item.Id == source.Id));
        var binding = await db.SourceDocumentBindings.SingleAsync(item =>
            item.SourceId == source.Id && item.ExternalKey == "delete-first-key");
        var document = await db.Documents.SingleAsync(item => item.Id == binding.DocumentId);
        Assert.Equal(Hash("delete-first-content"), document.Sha256);
        Assert.True(await blobs.ExistsAsync(document.Sha256, CancellationToken.None));
    }

    [Fact]
    public async Task Document_delete_waits_for_inflight_source_update_without_deadlock()
    {
        var blobRoot = Path.Combine(Path.GetTempPath(), "isestudio-source-sync-update-delete", Guid.NewGuid().ToString("N"));
        var innerBlobs = new LocalCasBlobStore(blobRoot);
        var blobs = new PausingFirstPutBlobStore(innerBlobs);
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var (source, actor) = await CreateSourceAndActorAsync(db);
        var oldBytes = Encoding.UTF8.GetBytes(Content("old-source-content"));
        await using var oldContent = new MemoryStream(oldBytes, writable: false);
        var oldBlob = await innerBlobs.PutAsync(oldContent, CancellationToken.None);
        var document = CreateDocument(source.Id, "old-source-content", "race.txt");
        document.ExternalKey = "race-key";
        document.Sha256 = oldBlob.Sha256;
        document.StoragePath = oldBlob.LegacyStoragePath;
        document.SizeBytes = oldBytes.LongLength;
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        db.SourceDocumentBindings.Add(new SourceDocumentBindingEntity
        {
            SourceId = source.Id,
            ExternalKey = "race-key",
            DocumentId = document.Id,
        });
        db.DocumentFileVersions.Add(new DocumentFileVersionEntity
        {
            DocumentId = document.Id,
            Version = 1,
            Sha256 = oldBlob.Sha256,
            SizeBytes = oldBytes.LongLength,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(source.Id, claim.SourceId);
        var runId = Assert.IsType<Guid>(claim.ActiveRunId);
        var coordinator = new SourceSyncCoordinator(contexts, blobs,
            [new SequenceSourceAdapter(ScanItems(Item("race-key", "new-source-content")))],
            jobs, TimeProvider.System);
        var syncTask = coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
        await blobs.PutStarted.WaitAsync(TimeSpan.FromSeconds(10));

        async Task<bool> DeleteDocumentAsync()
        {
            await using var deleteScope = services.CreateAsyncScope();
            var deleteProvider = deleteScope.ServiceProvider;
            var service = new DocumentService(
                deleteProvider.GetRequiredService<ISEStudioDbContext>(), TimeProvider.System,
                new KnowledgeSystemAccessService(), blobs, null!, null!,
                new ExtractionJobStore(
                    deleteProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(), TimeProvider.System));
            return await service.DeleteAsync(_fixture.KnowledgeSystemId, document.Id,
                new Actor(actor.Id.ToString()), CancellationToken.None);
        }

        var deleteTask = DeleteDocumentAsync();
        var deleteFinishedBeforePut = await Task.WhenAny(
            deleteTask, Task.Delay(TimeSpan.FromMilliseconds(250))) == deleteTask;
        blobs.AllowPut();
        var syncResult = await syncTask.WaitAsync(TimeSpan.FromSeconds(10));
        var deleted = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(deleteFinishedBeforePut);
        Assert.True(syncResult.IsComplete);
        Assert.Empty(syncResult.Errors);
        Assert.Equal(1, syncResult.Updated);
        Assert.True(deleted);
        await jobs.CompleteAsync(jobId, runId, syncResult, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.False(await db.Documents.AnyAsync(item => item.Id == document.Id));
        Assert.False(await db.SourceDocumentBindings.AnyAsync(binding => binding.DocumentId == document.Id));
        Assert.True(await db.Sources.AnyAsync(item => item.Id == source.Id));
        Assert.False(await innerBlobs.ExistsAsync(oldBlob.Sha256, CancellationToken.None));
        Assert.False(await innerBlobs.ExistsAsync(Hash("new-source-content"), CancellationToken.None));
        if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);
    }

    [Fact]
    public async Task Manual_document_reused_by_sync_is_never_marked_missing()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-manual-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var manualDocument = new DocumentEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Sha256 = Hash("manual-content"),
            OriginalFilename = "manual.txt",
            Folder = "/",
            Ext = "txt",
            SizeBytes = Encoding.UTF8.GetByteCount(Content("manual-content")),
            StoragePath = "manual/blob",
            UploadedAt = DateTimeOffset.UtcNow,
            IsManualUpload = true,
            ParseStatus = "parsed",
        };
        db.Sources.Add(source);
        db.Documents.Add(manualDocument);
        await db.SaveChangesAsync();
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var adapter = new SequenceSourceAdapter(
            ScanItems(Item("manual-key", "manual-content")), ScanItems());
        var coordinator = new SourceSyncCoordinator(contexts,
            scope.ServiceProvider.GetRequiredService<IBlobStore>(), [adapter], jobs, TimeProvider.System);

        async Task RunScanAsync()
        {
            var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
            var runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
            await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
        }

        await RunScanAsync();
        await RunScanAsync();
        var state = await ReadSourceStateAsync(contexts, source.Id);
        Assert.Equal(manualDocument.Id, Assert.Single(state.Bindings).DocumentId);
        Assert.True(Assert.Single(state.Documents).IsManualUpload);
        Assert.NotNull(Assert.Single(state.Bindings).MissingSince);
        Assert.Null(Assert.Single(state.Documents).MissingSince);
        Assert.Empty(state.FileVersions);
        Assert.Empty(state.ParseJobs);
    }

    [Fact]
    public async Task Deleting_source_recomputes_missing_state_from_remaining_bindings()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var (sourceToDelete, actor) = await CreateSourceAndActorAsync(db);
        var remainingSource = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"sync-remaining-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var now = DateTimeOffset.UtcNow;
        var sharedDocument = CreateDocument(sourceToDelete.Id, "shared-on-delete", "shared.txt");
        var unboundDocument = CreateDocument(sourceToDelete.Id, "unbound-on-delete", "unbound.txt");
        unboundDocument.MissingSince = now.AddDays(-1);
        db.Sources.Add(remainingSource);
        db.Documents.AddRange(sharedDocument, unboundDocument);
        db.SourceDocumentBindings.AddRange(
            new SourceDocumentBindingEntity
            {
                SourceId = sourceToDelete.Id,
                ExternalKey = "shared-deleted-key",
                DocumentId = sharedDocument.Id,
                MissingSince = now.AddDays(-1),
            },
            new SourceDocumentBindingEntity
            {
                SourceId = remainingSource.Id,
                ExternalKey = "shared-remaining-key",
                DocumentId = sharedDocument.Id,
                MissingSince = now.AddDays(-1),
            },
            new SourceDocumentBindingEntity
            {
                SourceId = sourceToDelete.Id,
                ExternalKey = "only-key",
                DocumentId = unboundDocument.Id,
                MissingSince = now.AddDays(-1),
            });
        await db.SaveChangesAsync();

        var status = await DeleteSourceAsync(sourceToDelete.Id, actor.Id);

        Assert.Equal(204, status);
        db.ChangeTracker.Clear();
        var persistedShared = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == sharedDocument.Id);
        var persistedUnbound = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == unboundDocument.Id);
        Assert.NotNull(persistedShared.MissingSince);
        Assert.Null(persistedUnbound.MissingSince);
        Assert.Single(await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.DocumentId == sharedDocument.Id).ToListAsync());
        Assert.False(await db.SourceDocumentBindings.AnyAsync(binding => binding.SourceId == sourceToDelete.Id));
    }

    [Fact]
    public async Task Deleting_document_removes_source_bindings_without_deleting_the_source()
    {
        var blobRoot = Path.Combine(Path.GetTempPath(), "isestudio-source-sync-delete-race", Guid.NewGuid().ToString("N"));
        var blobs = new PausingFirstRemovalBlobStore(new LocalCasBlobStore(blobRoot));
        await using var services = _fixture.BuildServices(collection =>
        {
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var (source, actor) = await CreateSourceAndActorAsync(db);
        var content = Encoding.UTF8.GetBytes(Content("delete-bound-document"));
        await using var contentStream = new MemoryStream(content, writable: false);
        var storedBlob = await blobs.PutAsync(contentStream, CancellationToken.None);
        var document = CreateDocument(source.Id, "delete-bound-document", "bound.txt");
        document.Sha256 = storedBlob.Sha256;
        document.StoragePath = storedBlob.LegacyStoragePath;
        document.SizeBytes = content.LongLength;
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        db.SourceDocumentBindings.Add(new SourceDocumentBindingEntity
        {
            SourceId = source.Id,
            ExternalKey = "bound-document-key",
            DocumentId = document.Id,
        });
        await db.SaveChangesAsync();

        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        async Task<bool> DeleteDocumentAsync()
        {
            await using var deleteScope = services.CreateAsyncScope();
            var deleteProvider = deleteScope.ServiceProvider;
            var deleteDb = deleteProvider.GetRequiredService<ISEStudioDbContext>();
            var service = new DocumentService(deleteDb, TimeProvider.System, new KnowledgeSystemAccessService(),
                blobs, null!, null!, new ExtractionJobStore(
                    deleteProvider.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>(), TimeProvider.System));
            return await service.DeleteAsync(_fixture.KnowledgeSystemId, document.Id,
                new Actor(actor.Id.ToString()), CancellationToken.None);
        }

        var deleteTask = DeleteDocumentAsync();
        await blobs.RemoveStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        var jobId = await jobs.EnqueueAsync(source.Id, CancellationToken.None);
        var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(source.Id, claim.SourceId);
        var runId = Assert.IsType<Guid>(claim.ActiveRunId);
        var coordinator = new SourceSyncCoordinator(contexts,
            blobs,
            [new SequenceSourceAdapter(ScanItems(Item("bound-document-key", "delete-bound-document")))],
            jobs, TimeProvider.System);
        var syncTask = coordinator.RunAsync(source.Id, jobId, runId, CancellationToken.None);
        var syncFinishedBeforeBlobRemoval = await Task.WhenAny(
            syncTask, Task.Delay(TimeSpan.FromMilliseconds(250))) == syncTask;
        var putCallsBeforeBlobRemoval = blobs.PutCallCount;
        blobs.AllowRemove();
        var deleted = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(deleted);
        Assert.False(syncFinishedBeforeBlobRemoval);
        Assert.Equal(1, putCallsBeforeBlobRemoval);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, blobs.PutCallCount);
        db.ChangeTracker.Clear();
        var recreatedBinding = await db.SourceDocumentBindings.SingleAsync(binding =>
            binding.SourceId == source.Id && binding.ExternalKey == "bound-document-key");
        Assert.NotEqual(document.Id, recreatedBinding.DocumentId);
        Assert.True(await db.Documents.AnyAsync(item => item.Id == recreatedBinding.DocumentId));
        Assert.True(await db.Sources.AnyAsync(item => item.Id == source.Id));
        Assert.True(await blobs.ExistsAsync(storedBlob.Sha256, CancellationToken.None));
        await jobs.CompleteAsync(jobId, runId, result, CancellationToken.None);
        if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);
    }

    private async Task<(SourceEntity Source, UserEntity Actor)> CreateSourceAndActorAsync(
        ISEStudioDbContext db)
    {
        var knowledgeSystem = await db.KnowledgeSystems.SingleAsync(
            item => item.Id == _fixture.KnowledgeSystemId);
        var actor = new UserEntity
        {
            Username = $"source-sync-owner-{Guid.NewGuid():N}",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "url",
            Name = $"source-sync-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        knowledgeSystem.OwnerId = actor.Id;
        db.Users.Add(actor);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return (source, actor);
    }

    private async Task<int> DeleteSourceAsync(Guid sourceId, Guid actorId)
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var actor = await db.Users.SingleAsync(user => user.Id == actorId);
        return (await CreateSourceService(db).DeleteAsync(
            _fixture.KnowledgeSystemId, sourceId, actor, CancellationToken.None)).StatusCode;
    }

    private async Task<int> WaitForBlockedSourceCommandAsync(bool isDelete, int blockerPid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await _fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pid
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND datname = current_database()
                  AND state = 'active'
                  AND wait_event_type = 'Lock'
                  AND @blocker_pid = ANY(pg_blocking_pids(pid))
                                    AND query ILIKE '%FOR UPDATE%'
                                    AND ((@is_delete AND query ILIKE '%AND knowledge_system_id =%')
                                        OR (NOT @is_delete AND query NOT ILIKE '%AND knowledge_system_id =%'))
                ORDER BY query_start
                LIMIT 1
                """;
            command.Parameters.AddWithValue("blocker_pid", blockerPid);
            command.Parameters.AddWithValue("is_delete", isDelete);
            if (await command.ExecuteScalarAsync() is int pid) return pid;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException("The expected source row-lock waiter did not appear.");
    }

    private static SourceService CreateSourceService(ISEStudioDbContext db)
        => new(db, new KnowledgeSystemAccessService(), new SourceAdapterRegistry([]),
            new SourceSecretProtector(new ConfigurationBuilder().Build()), TimeProvider.System);

    private static async Task<SourceState> ReadSourceStateAsync(
        IDbContextFactory<ISEStudioDbContext> contexts, Guid sourceId)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var documentIds = await db.SourceDocumentBindings.AsNoTracking()
            .Where(binding => binding.SourceId == sourceId)
            .Select(binding => binding.DocumentId)
            .Distinct()
            .ToListAsync();
        return new SourceState(
            await db.Documents.AsNoTracking().Where(document => documentIds.Contains(document.Id)).ToListAsync(),
            await db.SourceDocumentBindings.AsNoTracking().Where(binding => binding.SourceId == sourceId).ToListAsync(),
            await db.DocumentFileVersions.AsNoTracking().Where(version => documentIds.Contains(version.DocumentId))
                .ToListAsync(),
            await db.DocumentParseJobs.AsNoTracking().Where(job => documentIds.Contains(job.DocumentId)).ToListAsync());
    }

    private DocumentEntity CreateDocument(Guid sourceId, string shaSeed, string filename)
        => new()
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            SourceId = sourceId,
            ExternalKey = shaSeed,
            IsManualUpload = false,
            Sha256 = Hash(shaSeed),
            OriginalFilename = filename,
            Folder = "/",
            Ext = Path.GetExtension(filename).TrimStart('.'),
            SizeBytes = 1,
            StoragePath = $"{shaSeed}/blob",
            UploadedAt = DateTimeOffset.UtcNow,
            MissingSince = null,
        };

    private SourceScan Scan(string content)
        => ScanItems(Item("remote-key-a", content));

    private SourceScan ScanItems(params SourceItem[] items)
        => new(EnumerateItems(items), IsComplete: true);

    private SourceItem Item(string externalKey, string content)
        => new(externalKey, "remote.txt", "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes(Content(content))), DateTimeOffset.UtcNow);

    private string Content(string value) => $"{_contentNamespace}:{value}";

    private static async IAsyncEnumerable<SourceItem> EnumerateItems(
        IReadOnlyList<SourceItem> items, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<SourceItem> EnumerateThenThrow(
        SourceItem item, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
        await Task.Yield();
        throw new IOException("page failed after the first item");
    }

    private string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Content(value))));

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed record SourceState(
        IReadOnlyList<DocumentEntity> Documents,
        IReadOnlyList<SourceDocumentBindingEntity> Bindings,
        IReadOnlyList<DocumentFileVersionEntity> FileVersions,
        IReadOnlyList<DocumentParseJobEntity> ParseJobs);

    private sealed class SequenceSourceAdapter(params SourceScan[] scans) : ISourceAdapter
    {
        private readonly Queue<SourceScan> _scans = new(scans);

        public string Kind => "url";

        public Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_scans.TryDequeue(out var scan))
                throw new InvalidOperationException("No test scan remains.");
            return Task.FromResult(scan);
        }
    }

    private sealed class TwoPartyScanGate
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _bothArrived.TrySetResult();
            await _bothArrived.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatedSourceAdapter(SourceScan scan, TwoPartyScanGate gate) : ISourceAdapter
    {
        public string Kind => "url";

        public async Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return scan;
        }
    }

    private sealed class PausingFirstRemovalBlobStore(IBlobStore inner) : IBlobStore
    {
        private readonly TaskCompletionSource _removeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowRemove = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _putCallCount;
        private int _removeCallCount;

        public Task RemoveStarted => _removeStarted.Task;
        public int PutCallCount => Volatile.Read(ref _putCallCount);

        public async Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _putCallCount);
            return await inner.PutAsync(content, cancellationToken);
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

    private sealed class PausingFirstPutBlobStore(IBlobStore inner) : IBlobStore
    {
        private readonly TaskCompletionSource _putStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowPut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _putCallCount;

        public Task PutStarted => _putStarted.Task;

        public async Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _putCallCount) == 1)
            {
                _putStarted.TrySetResult();
                await _allowPut.Task.WaitAsync(cancellationToken);
            }
            return await inner.PutAsync(content, cancellationToken);
        }

        public Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken)
            => inner.GetAsync(sha256, cancellationToken);

        public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken)
            => inner.ExistsAsync(sha256, cancellationToken);

        public Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
            => inner.RemoveAsync(sha256, cancellationToken);

        public void AllowPut() => _allowPut.TrySetResult();
    }
}