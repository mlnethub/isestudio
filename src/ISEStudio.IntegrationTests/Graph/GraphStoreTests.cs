using ISEStudio.Graph;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data.Common;

namespace ISEStudio.IntegrationTests.Graph;

public sealed class GraphStoreTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public GraphStoreTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Record_fact_persists_fact_and_evidence_in_one_transaction()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 2);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var fact = await store.RecordFactAsync(command, CancellationToken.None);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.Facts.CountAsync(item => item.Id == fact.Id));
        Assert.Equal(2, await verify.FactEvidence.CountAsync(item => item.FactId == fact.Id));

        var audit = await verify.AuditEvents.SingleAsync(item =>
            item.KnowledgeSystemId == _fixture.KnowledgeSystemId &&
            item.Action == "graph.fact.recorded");

        Assert.Equal("graph.fact.recorded", audit.Action);
        Assert.NotNull(audit.Detail);
        Assert.Equal(fact.Id, audit.Detail!.RootElement.GetProperty("factId").GetGuid());
        Assert.Equal(_fixture.KnowledgeSystemId, audit.Detail.RootElement.GetProperty("knowledgeSystemId").GetGuid());
        Assert.Equal(2, audit.Detail.RootElement.GetProperty("evidenceCount").GetInt32());
    }

    [Fact]
    public async Task Record_fact_rolls_back_when_evidence_references_unknown_chunk()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1) with
        {
            Evidence = [new FactEvidenceInput(Guid.NewGuid(), "quote", "predicate")]
        };

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordFactAsync(command, CancellationToken.None));

        Assert.Contains("chunk", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = CreateDbContext();
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.FactEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents
            .Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .ToListAsync());
    }

    [Fact]
    public async Task Record_fact_rejects_evidence_chunk_from_different_knowledge_system()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var foreignChunkId = await CreateForeignChunkAsync();
        var command = NewEntityObjectFact(evidenceCount: 1) with
        {
            Evidence = [new FactEvidenceInput(foreignChunkId, "quote", "predicate")]
        };

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordFactAsync(command, CancellationToken.None));

        Assert.Contains("does not belong to knowledge system", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = CreateDbContext();
        Assert.Empty(await verify.Facts.ToListAsync());
        Assert.Empty(await verify.FactEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents
            .Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId)
            .ToListAsync());
    }

    [Fact]
    public async Task Invalidate_fact_marks_live_fact_in_same_knowledge_system_and_writes_audit()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var fact = await store.RecordFactAsync(command, CancellationToken.None);
        var invalidatedAt = DateTimeOffset.UtcNow;

        await store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, invalidatedAt, CancellationToken.None);

        await using var verify = CreateDbContext();
        var persistedFact = await verify.Facts.SingleAsync(item => item.Id == fact.Id);
        Assert.Equal(TruncateToPostgresPrecision(invalidatedAt), persistedFact.InvalidatedAt);

        var audit = await verify.AuditEvents.SingleAsync(item =>
            item.KnowledgeSystemId == _fixture.KnowledgeSystemId &&
            item.Action == "graph.fact.invalidated");

        Assert.Equal("graph.fact.invalidated", audit.Action);
        Assert.NotNull(audit.Detail);
        Assert.Equal(fact.Id, audit.Detail!.RootElement.GetProperty("factId").GetGuid());
        Assert.Equal(_fixture.KnowledgeSystemId, audit.Detail.RootElement.GetProperty("knowledgeSystemId").GetGuid());
    }

    [Fact]
    public async Task Invalidate_fact_throws_when_fact_is_already_invalidated_or_in_another_knowledge_system()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        var fact = await store.RecordFactAsync(command, CancellationToken.None);
        await store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var otherKnowledgeSystemId = await CreateKnowledgeSystemAsync("graph-store-other-ks");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.InvalidateFactAsync(_fixture.KnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.InvalidateFactAsync(otherKnowledgeSystemId, fact.Id, DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task Invalidate_fact_allows_only_one_concurrent_success_and_one_audit_record()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();
        var command = NewEntityObjectFact(evidenceCount: 1);

        await using var seedServices = BuildServices();
        await using var seedScope = seedServices.CreateAsyncScope();
        var seedStore = seedScope.ServiceProvider.GetRequiredService<IGraphStore>();
        var fact = await seedStore.RecordFactAsync(command, CancellationToken.None);

        var barrier = new MatchingCommandBarrierInterceptor(2, IsInvalidateFactCommand);
        await using var db1 = CreateDbContext(barrier);
        await using var db2 = CreateDbContext(barrier);
        var store1 = new GraphStore(db1);
        var store2 = new GraphStore(db2);
        var invalidatedAt = DateTimeOffset.UtcNow;

        var outcomes = await Task.WhenAll(
            AttemptInvalidateAsync(store1, _fixture.KnowledgeSystemId, fact.Id, invalidatedAt),
            AttemptInvalidateAsync(store2, _fixture.KnowledgeSystemId, fact.Id, invalidatedAt));

        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        Assert.IsType<KeyNotFoundException>(Assert.Single(outcomes, outcome => outcome is not null));

        await using var verify = CreateDbContext();
        Assert.NotNull(await verify.Facts.SingleAsync(item => item.Id == fact.Id && item.InvalidatedAt != null));

        var invalidationAudits = await verify.AuditEvents
            .Where(item =>
                item.KnowledgeSystemId == _fixture.KnowledgeSystemId &&
                item.Action == "graph.fact.invalidated")
            .ToListAsync();

        Assert.Single(invalidationAudits, item =>
            item.Detail is not null &&
            item.Detail.RootElement.TryGetProperty("factId", out var factIdElement) &&
            factIdElement.GetGuid() == fact.Id);
    }

    [Fact]
    public async Task Neighborhood_uses_bounded_traversal_and_effective_time()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var rootEntityId = _fixture.SubjectEntityId;
        var middleEntityId = _fixture.ObjectEntityId;
        var leafEntityId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");

        await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            rootEntityId,
            middleEntityId,
            validFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            middleEntityId,
            leafEntityId,
            validFrom: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                rootEntityId,
                2,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                false),
            CancellationToken.None);

        Assert.Contains(middleEntityId, result.EntityIds);
        Assert.DoesNotContain(leafEntityId, result.EntityIds);
        Assert.Single(result.Facts);
    }

    [Fact]
    public async Task Neighborhood_finds_entity_that_only_appears_as_object_when_root_is_queried()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var reverseNeighborId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");
        var rootFactId = await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            _fixture.SubjectEntityId,
            _fixture.ObjectEntityId);
        var reverseFactId = await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            reverseNeighborId,
            _fixture.ObjectEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                2,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        Assert.Contains(reverseNeighborId, result.EntityIds);
        Assert.Contains(result.Facts, fact => fact.Id == rootFactId);
        Assert.Contains(result.Facts, fact => fact.Id == reverseFactId);
    }

    [Fact]
    public async Task Neighborhood_finds_fact_and_neighbor_when_root_only_has_incoming_edge()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var incomingNeighborId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");
        var incomingFactId = await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            incomingNeighborId,
            _fixture.SubjectEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                1,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        var fact = Assert.Single(result.Facts, item => item.Id == incomingFactId);
        Assert.Equal(incomingNeighborId, fact.SubjectEntityId);
        Assert.Equal(_fixture.SubjectEntityId, fact.ObjectEntityId);
        Assert.Contains(incomingNeighborId, result.EntityIds);
    }

    [Fact]
    public async Task Neighborhood_does_not_return_root_self_loop()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var selfLoopFactId = await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            _fixture.SubjectEntityId,
            _fixture.SubjectEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                1,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        Assert.DoesNotContain(result.Facts, fact => fact.Id == selfLoopFactId);
        Assert.Equal([_fixture.SubjectEntityId], result.EntityIds);
    }

    [Fact]
    public async Task Neighborhood_traverses_both_directions_once_and_excludes_edge_back_to_root()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var middleEntityId = _fixture.ObjectEntityId;
        var leafEntityId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");
        var forwardFactId = await InsertFactAsync(_fixture.KnowledgeSystemId, _fixture.SubjectEntityId, middleEntityId);
        var reverseFactId = await InsertFactAsync(_fixture.KnowledgeSystemId, leafEntityId, middleEntityId);
        var rootLoopFactId = await InsertFactAsync(_fixture.KnowledgeSystemId, middleEntityId, _fixture.SubjectEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                3,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        Assert.Contains(middleEntityId, result.EntityIds);
        Assert.Contains(leafEntityId, result.EntityIds);
        Assert.Contains(result.Facts, fact => fact.Id == forwardFactId);
        Assert.Contains(result.Facts, fact => fact.Id == reverseFactId);
        Assert.DoesNotContain(result.Facts, fact => fact.Id == rootLoopFactId);
        Assert.Equal(result.Facts.Count, result.Facts.Select(fact => fact.Id).Distinct().Count());
    }

    [Fact]
    public async Task Neighborhood_does_not_treat_json_values_as_adjacent_entities()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var jsonFactId = await InsertJsonFactAsync(_fixture.KnowledgeSystemId, _fixture.SubjectEntityId, "{\"value\":\"json\"}");

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                1,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        Assert.DoesNotContain(result.Facts, fact => fact.Id == jsonFactId);
        Assert.DoesNotContain(result.Facts, fact => fact.ObjectKind == GraphObjectKind.JsonValue);
    }

    [Fact]
    public async Task Neighborhood_does_not_resolve_entity_type_from_another_knowledge_system()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var foreignKnowledgeSystemId = await CreateKnowledgeSystemAsync($"graph-store-type-{Guid.NewGuid():N}");
        var foreignEntityTypeId = await CreateEntityTypeAsync(foreignKnowledgeSystemId, "foreign-type");
        var localEntityId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Local entity", foreignEntityTypeId);
        await InsertFactAsync(_fixture.KnowledgeSystemId, _fixture.SubjectEntityId, localEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(
                _fixture.KnowledgeSystemId,
                _fixture.SubjectEntityId,
                1,
                DateTimeOffset.UtcNow,
                false),
            CancellationToken.None);

        var localEntity = Assert.Single(result.Entities, entity => entity.Id == localEntityId);
        Assert.Null(localEntity.TypeKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Neighborhood_rejects_max_depth_outside_supported_range(int maxDepth)
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.GetNeighborhoodAsync(
                new GraphNeighborhoodQuery(
                    _fixture.KnowledgeSystemId,
                    _fixture.SubjectEntityId,
                    maxDepth,
                    DateTimeOffset.UtcNow,
                    false),
                CancellationToken.None));

        Assert.Equal("MaxDepth", exception.ParamName);
    }

    [Fact]
    public async Task Neighborhood_excludes_invalidated_facts_unless_requested()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var rootEntityId = _fixture.SubjectEntityId;
        var liveNeighborId = _fixture.ObjectEntityId;
        var invalidatedNeighborId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");

        await InsertFactAsync(_fixture.KnowledgeSystemId, rootEntityId, liveNeighborId);
        var invalidatedFactId = await InsertFactAsync(
            _fixture.KnowledgeSystemId,
            rootEntityId,
            invalidatedNeighborId,
            invalidatedAt: DateTimeOffset.UtcNow);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var liveOnly = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(_fixture.KnowledgeSystemId, rootEntityId, 1, DateTimeOffset.UtcNow, false),
            CancellationToken.None);
        var withInvalidated = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(_fixture.KnowledgeSystemId, rootEntityId, 1, DateTimeOffset.UtcNow, true),
            CancellationToken.None);

        Assert.DoesNotContain(invalidatedNeighborId, liveOnly.EntityIds);
        Assert.DoesNotContain(liveOnly.Facts, fact => fact.Id == invalidatedFactId);

        Assert.Contains(invalidatedNeighborId, withInvalidated.EntityIds);
        Assert.Contains(withInvalidated.Facts, fact => fact.Id == invalidatedFactId && fact.Status == FactStatus.Invalidated);
    }

    [Fact]
    public async Task Neighborhood_never_crosses_knowledge_system_boundaries()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var foreignKnowledgeSystemId = await CreateKnowledgeSystemAsync($"graph-store-neighborhood-{Guid.NewGuid():N}");
        var foreignEntityId = await CreateGraphEntityAsync(foreignKnowledgeSystemId, "Foreign Pump");
        await InsertFactAsync(_fixture.KnowledgeSystemId, _fixture.SubjectEntityId, foreignEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(_fixture.KnowledgeSystemId, _fixture.SubjectEntityId, 5, DateTimeOffset.UtcNow, false),
            CancellationToken.None);

        Assert.DoesNotContain(foreignEntityId, result.EntityIds);
        Assert.DoesNotContain(result.Facts, fact => fact.ObjectEntityId == foreignEntityId);
    }

    [Fact]
    public async Task Neighborhood_excludes_cycle_edges_and_deduplicates_entities()
    {
        await _fixture.SeedGraphReferencesAsync();
        await ResetGraphWritesAsync();

        var rootEntityId = _fixture.SubjectEntityId;
        var leftEntityId = _fixture.ObjectEntityId;
        var rightEntityId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump C");
        var sharedEntityId = await CreateGraphEntityAsync(_fixture.KnowledgeSystemId, "Pump D");

        await InsertFactAsync(_fixture.KnowledgeSystemId, rootEntityId, leftEntityId);
        await InsertFactAsync(_fixture.KnowledgeSystemId, rootEntityId, rightEntityId);
        await InsertFactAsync(_fixture.KnowledgeSystemId, leftEntityId, sharedEntityId);
        await InsertFactAsync(_fixture.KnowledgeSystemId, rightEntityId, sharedEntityId);
        await InsertFactAsync(_fixture.KnowledgeSystemId, sharedEntityId, rootEntityId);

        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        var result = await store.GetNeighborhoodAsync(
            new GraphNeighborhoodQuery(_fixture.KnowledgeSystemId, rootEntityId, 5, DateTimeOffset.UtcNow, false),
            CancellationToken.None);

        Assert.Equal(result.EntityIds.Count, result.EntityIds.Distinct().Count());
        Assert.Equal(1, result.EntityIds.Count(id => id == sharedEntityId));
        Assert.DoesNotContain(result.Facts, fact => fact.SubjectEntityId == sharedEntityId && fact.ObjectEntityId == rootEntityId);
        Assert.Equal(4, result.Facts.Count);
    }

    private RecordFactCommand NewEntityObjectFact(int evidenceCount)
    {
        return new RecordFactCommand(
            _fixture.KnowledgeSystemId,
            _fixture.SubjectEntityId,
            _fixture.PredicateId,
            GraphObjectKind.Entity,
            _fixture.ObjectEntityId,
            null,
            0.9m,
            null,
            null,
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, evidenceCount)
                .Select(_ => new FactEvidenceInput(_fixture.ChunkId, "fixture quote", "supports"))
                .ToArray(),
            null);
    }

    private async Task ResetGraphWritesAsync()
    {
        await using var db = CreateDbContext();
        db.FactEvidence.RemoveRange(db.FactEvidence);
        db.Facts.RemoveRange(db.Facts);
        db.AuditEvents.RemoveRange(db.AuditEvents.Where(item => item.KnowledgeSystemId == _fixture.KnowledgeSystemId));
        db.Chunks.RemoveRange(db.Chunks.Where(item => item.Id != _fixture.ChunkId));
        db.Documents.RemoveRange(db.Documents.Where(item => item.KnowledgeSystemId != _fixture.KnowledgeSystemId));
        db.KnowledgeSystems.RemoveRange(db.KnowledgeSystems.Where(item => item.Id != _fixture.KnowledgeSystemId));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateForeignChunkAsync()
    {
        var otherKnowledgeSystemId = await CreateKnowledgeSystemAsync("graph-store-foreign-chunk");
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        db.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            KnowledgeSystemId = otherKnowledgeSystemId,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            OriginalFilename = "foreign.txt",
            Folder = "/",
            Ext = "txt",
            SizeBytes = 14,
            StoragePath = $"foreign/{documentId:N}",
            UploadedAt = now,
        });
        db.Chunks.Add(new ChunkEntity
        {
            Id = chunkId,
            DocumentId = documentId,
            Idx = 0,
            Text = "foreign chunk",
            CharStart = 0,
            CharEnd = 13,
            TokenEstimate = 2,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        return chunkId;
    }

    private async Task<Guid> CreateKnowledgeSystemAsync(string publicId)
    {
        var knowledgeSystemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = knowledgeSystemId,
            PublicId = publicId,
            Name = publicId,
            Description = "Fixture knowledge system",
            GraphIri = $"https://example.test/{publicId}",
            BaseIri = $"https://example.test/{publicId}#",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return knowledgeSystemId;
    }

    private async Task<Guid> CreateGraphEntityAsync(Guid knowledgeSystemId, string label, Guid? entityTypeId = null)
    {
        var entityId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.GraphEntities.Add(new GraphEntityEntity
        {
            Id = entityId,
            KnowledgeSystemId = knowledgeSystemId,
            EntityTypeId = entityTypeId,
            Label = label,
            Description = $"Fixture entity {label}",
        });
        await db.SaveChangesAsync();

        return entityId;
    }

    private async Task<Guid> CreateEntityTypeAsync(Guid knowledgeSystemId, string key)
    {
        var entityTypeId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.EntityTypes.Add(new EntityTypeEntity
        {
            Id = entityTypeId,
            KnowledgeSystemId = knowledgeSystemId,
            Key = key,
            Label = key,
            Description = "Fixture entity type",
        });
        await db.SaveChangesAsync();

        return entityTypeId;
    }

    private async Task<Guid> InsertJsonFactAsync(Guid knowledgeSystemId, Guid subjectEntityId, string objectValue)
    {
        var factId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Facts.Add(new FactEntity
        {
            Id = factId,
            KnowledgeSystemId = knowledgeSystemId,
            SubjectEntityId = subjectEntityId,
            PredicateId = _fixture.PredicateId,
            ObjectValue = objectValue,
            Confidence = 0.9m,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return factId;
    }

    private async Task<Guid> InsertFactAsync(
        Guid knowledgeSystemId,
        Guid subjectEntityId,
        Guid objectEntityId,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        DateTimeOffset? invalidatedAt = null)
    {
        var factId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Facts.Add(new FactEntity
        {
            Id = factId,
            KnowledgeSystemId = knowledgeSystemId,
            SubjectEntityId = subjectEntityId,
            PredicateId = _fixture.PredicateId,
            ObjectEntityId = objectEntityId,
            Confidence = 0.9m,
            ValidFrom = validFrom,
            ValidTo = validTo,
            RecordedAt = DateTimeOffset.UtcNow,
            InvalidatedAt = invalidatedAt,
        });
        await db.SaveChangesAsync();

        return factId;
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % 10), value.Offset);
    }

    private static async Task<Exception?> AttemptInvalidateAsync(
        IGraphStore store,
        Guid knowledgeSystemId,
        Guid factId,
        DateTimeOffset invalidatedAt)
    {
        try
        {
            await store.InvalidateFactAsync(knowledgeSystemId, factId, invalidatedAt, CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool IsInvalidateFactCommand(DbCommand command)
    {
        var sql = command.CommandText.Replace("\r", " ").Replace("\n", " ").Replace("\"", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return sql.Contains("facts", StringComparison.Ordinal) &&
               sql.Contains("invalidated_at is null", StringComparison.Ordinal) &&
               (sql.Contains("select", StringComparison.Ordinal) || sql.Contains("update", StringComparison.Ordinal));
    }

    private ISEStudioDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(GetConnectionString());

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        var options = builder.Options;
        return new ISEStudioDbContext(options);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(GetConnectionString())
                .Options;
            return new PgDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddGraphStore();
        return services.BuildServiceProvider();
    }

    private string GetConnectionString()
    {
        using var connection = _fixture.OpenConnectionAsync().GetAwaiter().GetResult();
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
        {
            Password = "postgres",
            PersistSecurityInfo = true,
        };

        return builder.ConnectionString;
    }

    private sealed class PgDbContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;

        public PgDbContextFactory(DbContextOptions<ISEStudioDbContext> options)
        {
            _options = options;
        }

        public ISEStudioDbContext CreateDbContext() => new(_options);
    }

    private sealed class MatchingCommandBarrierInterceptor : DbCommandInterceptor
    {
        private readonly int _participantCount;
        private readonly Func<DbCommand, bool> _predicate;
        private readonly TaskCompletionSource _allParticipantsArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _matchCount;

        public MatchingCommandBarrierInterceptor(int participantCount, Func<DbCommand, bool> predicate)
        {
            _participantCount = participantCount;
            _predicate = predicate;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_predicate(command))
            {
                var arrival = Interlocked.Increment(ref _matchCount);
                if (arrival <= _participantCount)
                {
                    if (arrival == _participantCount)
                    {
                        _allParticipantsArrived.TrySetResult();
                    }

                    await _allParticipantsArrived.Task.WaitAsync(cancellationToken);
                }
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}