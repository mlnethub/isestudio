using System.Diagnostics;
using System.Text;
using ISEStudio.Audit;
using ISEStudio.Conflicts;
using ISEStudio.Extraction;
using ISEStudio.Documents;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Application.Foundation;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Ontology;
using ISEStudio.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ISEStudio.IntegrationTests.Ingestion;

[Collection(SourceSyncTestCollection.Name)]
public sealed class SourceStatementConnectorTests(PostgresGraphFixture fixture) : IClassFixture<PostgresGraphFixture>
{
    [Fact]
    public async Task Long_incompressible_literal_and_all_long_terms_commit_and_concurrent_retry_deduplicates()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var huge = seed.Input with { Id = "huge", Object = RandomText(8192) };
        var result = await Batch(contexts, secrets, seed, [seed.Input, huge, seed.Input with { Id = "last", Object = "last" }]);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(new[] { 200, 200, 200 }, result.Value!.Results.Select(item => item.Status));
        var allLong = seed with { Input = seed.Input with { Id = "long-terms", Subject = "urn:" + RandomText(2044),
            Predicate = "urn:" + RandomText(2044), Object = "urn:" + RandomText(2044), ObjectKind = "iri" } };
        var accepted = await Task.WhenAll(Push(contexts, secrets, allLong), Push(contexts, secrets, allLong));
        Assert.All(accepted, item => Assert.Equal(200, item.Status));
        Assert.Equal(1, accepted.Sum(item => item.Added));
        await using var db = await contexts.CreateDbContextAsync();
        Assert.Equal(4, await db.SourceStatements.CountAsync(item => item.SourceId == seed.Source.Id));
        Assert.Equal(huge.Object, (await db.WorkspaceStatements.SingleAsync(item => item.Subject == huge.Subject && item.Object == huge.Object)).Object);
    }

    [Fact]
    public async Task Blank_nodes_are_stable_within_a_source_and_isolated_between_sources()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var first = await Seed(contexts, secrets);
        var second = await Seed(contexts, secrets);
        var firstFact = first.Input with { Subject = "b1", SubjectKind = "blank", Object = "b2", ObjectKind = "blank" };
        var secondFact = firstFact with { Id = "second", Predicate = "urn:another" };
        Assert.Equal(200, (await Push(contexts, secrets, first with { Input = firstFact })).Status);
        Assert.Equal(200, (await Push(contexts, secrets, first with { Input = secondFact })).Status);
        Assert.Equal(200, (await Push(contexts, secrets, second with { Input = firstFact })).Status);
        Assert.Equal(200, (await Push(contexts, secrets, first with { Input = firstFact })).Status);

        await using var db = await contexts.CreateDbContextAsync();
        var facts = await db.WorkspaceStatements.Where(item => item.ObjectKind == "blank").OrderBy(item => item.Predicate).ToListAsync();
        Assert.Equal(3, facts.Count);
        Assert.Equal(facts[0].Subject, facts[1].Subject);
        Assert.Equal(facts[0].Object, facts[1].Object);
        Assert.NotEqual(facts[0].Subject, facts[2].Subject);
        Assert.NotEqual(facts[0].Object, facts[2].Object);
    }

    [Fact]
    public async Task Existing_explicit_xsd_string_fact_is_reused_by_plain_string_push()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var ks = await db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == fixture.KnowledgeSystemId);
            db.WorkspaceStatements.Add(new WorkspaceStatementEntity
            {
                KnowledgeSystemId = ks.Id,
                Layer = "ABox",
                GraphIri = ks.GraphIri.TrimEnd('/') + "/abox",
                Subject = seed.Input.Subject,
                SubjectKind = "iri",
                Predicate = seed.Input.Predicate,
                Object = seed.Input.Object,
                ObjectKind = "literal",
                Datatype = "http://www.w3.org/2001/XMLSchema#string",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var accepted = await Push(contexts, secrets, seed);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(0, accepted.Added);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Single(await verify.WorkspaceStatements.Where(item => item.Subject == seed.Input.Subject
            && item.Predicate == seed.Input.Predicate && item.Object == seed.Input.Object).ToListAsync());
        Assert.Single(await verify.SourceStatements.Where(item => item.SourceId == seed.Source.Id).ToListAsync());
        Assert.Single(await verify.SourceStatementFacts.Where(item => item.SourceStatementId == accepted.StatementId).ToListAsync());
    }

    [Fact]
    public async Task Batch_database_failure_rolls_back_one_item_and_continues_without_sensitive_error()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var injecting = await InjectingFactory(contexts, new BatchSaveFailure());
        var result = await Batch(injecting, secrets, seed, [seed.Input,
            seed.Input with { Id = "fault", Object = "SENSITIVE-BODY" }, seed.Input with { Id = "last", Object = "last" }]);
        Assert.Equal(207, result.StatusCode);
        Assert.Equal(new[] { 200, 500, 200 }, result.Value!.Results.Select(item => item.Status));
        Assert.DoesNotContain("SENSITIVE", JsonSerializer.Serialize(result));
        await using var db = await contexts.CreateDbContextAsync();
        Assert.Equal(2, await db.SourceStatements.CountAsync(item => item.SourceId == seed.Source.Id));
        Assert.False(await db.WorkspaceStatements.AnyAsync(item => item.Subject == seed.Input.Subject && item.Object == "SENSITIVE-BODY"));
        Assert.Equal(2, await db.SourceStatementFacts.CountAsync(item => db.SourceStatements
            .Any(record => record.Id == item.SourceStatementId && record.SourceId == seed.Source.Id)));
    }

    [Fact]
    public async Task Bounded_workspace_indexes_match_after_fresh_and_Task7_upgrade_migrations()
    {
        var fresh = await fixture.GetIndexDefinitionsAsync("workspace_statements");
        Assert.DoesNotContain(fresh.Values, definition => definition.Contains("\"Object\"") || definition.Contains("\"Subject\""));
        await using var connection = await fixture.OpenConnectionAsync();
        var database = "upgrade_" + Guid.NewGuid().ToString("N");
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE {database}";
            await create.ExecuteNonQueryAsync();
        }
        var upgradeConnectionString = fixture.GetConnectionString(database);
        await using var db = new ISEStudioDbContext(new DbContextOptionsBuilder<ISEStudioDbContext>().UseNpgsql(upgradeConnectionString).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260930213301_AddSourceStatements");
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        await using var upgraded = new Npgsql.NpgsqlConnection(upgradeConnectionString);
        await upgraded.OpenAsync();
        await using var query = upgraded.CreateCommand();
        query.CommandText = "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'workspace_statements'";
        await using var reader = await query.ExecuteReaderAsync();
        var indexes = new Dictionary<string, string>();
        while (await reader.ReadAsync()) indexes.Add(reader.GetString(0), reader.GetString(1));
        Assert.Equal(fresh.OrderBy(item => item.Key), indexes.OrderBy(item => item.Key));
    }

    private static string RandomText(int length) => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator
        .GetBytes(length)).Replace('+', 'a').Replace('/', 'b')[..length];

    private async Task<SourceMutationResult<SourceStatementsResult>> Batch(IDbContextFactory<ISEStudioDbContext> contexts,
        ISourceSecretProtector secrets, Seeded seed, SourceStatementRequest[] items)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var sources = Sources(db, secrets);
        var service = new SourcePushService(sources, null!, new SourceStatementService(contexts, sources, TimeProvider.System));
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + seed.Token;
        await using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { statements = items },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        http.Request.Body = body;
        return await service.PushStatementsAsync(fixture.KnowledgeSystemId, seed.Source.Id, http.Request, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_document_delete_and_source_delete_complete_without_KS_FK_deadlock(bool sourceFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var document = new DocumentEntity { KnowledgeSystemId = fixture.KnowledgeSystemId, SourceId = seed.Source.Id,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), OriginalFilename = "delete.txt",
            Ext = "txt", Folder = "/", StoragePath = "unused", UploadedAt = DateTimeOffset.UtcNow };
        await using (var db = await contexts.CreateDbContextAsync())
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync();
        }
        var auditBarrier = new DocumentAuditBarrier();
        var injecting = await InjectingFactory(contexts, auditBarrier);
        async Task<bool> DeleteDocument()
        {
            await using var db = await injecting.CreateDbContextAsync();
            return await new DocumentService(db, TimeProvider.System, new KnowledgeSystemAccessService(),
                services.GetRequiredService<IBlobStore>(), services.GetRequiredService<IDocumentParser>(),
                services.GetRequiredService<Chunker>(), new ExtractionJobStore(contexts, TimeProvider.System))
                .DeleteAsync(fixture.KnowledgeSystemId, document.Id, new Actor(seed.Actor.Id.ToString()), default);
        }
        Task<bool> deletion;
        Task<int> sourceDeletion;
        if (!sourceFirst)
        {
            deletion = DeleteDocument();
            var docPid = await auditBarrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            sourceDeletion = Mutate(contexts, secrets, seed, "delete");
            try { await WaitForWaiter(docPid, "document"); }
            finally { auditBarrier.Release.TrySetResult(); }
        }
        else
        {
            await using var blocker = await fixture.OpenConnectionAsync();
            await using var barrier = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = barrier;
                command.CommandText = "SELECT id FROM document WHERE id = @id FOR UPDATE";
                command.Parameters.AddWithValue("id", document.Id);
                await command.ExecuteScalarAsync();
            }
            sourceDeletion = Mutate(contexts, secrets, seed, "delete");
            var sourcePid = await WaitForWaiter(blocker.ProcessID, "document");
            deletion = DeleteDocument();
            try { await WaitForWaiter(sourcePid, "document"); }
            finally { auditBarrier.Release.TrySetResult(); await barrier.CommitAsync(); }
        }
        Assert.True(await deletion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(204, await sourceDeletion.WaitAsync(TimeSpan.FromSeconds(15)));
        await using var state = await contexts.CreateDbContextAsync();
        Assert.False(await state.Documents.AnyAsync(item => item.Id == document.Id));
        Assert.False(await state.Sources.AnyAsync(item => item.Id == seed.Source.Id));
    }

    [Fact]
    public async Task Production_DI_automatic_terminology_import_commits_with_caller_repository()
    {
        await using var services = fixture.BuildServices(collection =>
        {
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<KnowledgeSystemAccessService>();
            collection.AddSingleton<ExtractionJobStore>();
            collection.AddScoped<AuditLogService>();
            collection.AddOntologyServices();
            collection.AddConflictServices();
            collection.AddExtractionServices();
            collection.AddVocabularyServices();
            collection.AddScoped<ABoxValidator>();
            collection.Configure<ISEStudio.Configuration.ISEStudioOptions>(options => options.AutomaticTerminology = true);
        });
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        using var secrets = Secrets();
        var seed = await Seed(contexts, secrets);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        db.Database.SetCommandTimeout(3);
        var ks = new KnowledgeSystemEntity { Name = "terminology", PublicId = "terms-" + Guid.NewGuid().ToString("N"),
            GraphIri = "urn:terms:" + Guid.NewGuid(), BaseIri = "https://example.test/terms#", CreatedAt = DateTimeOffset.UtcNow };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        var repository = scope.ServiceProvider.GetRequiredService<IRdfStatementRepository>();
        await repository.ReplaceLayerAsync(ks.Id, "TBox", [
            new(new RdfIri(ks.BaseIri + "Pump"), Vocabulary.RdfType, new RdfIri(Vocabulary.OwlClass), ks.GraphIri),
            new(new RdfIri(ks.BaseIri + "Pump"), Vocabulary.RdfsLabel, new RdfLiteral("Pump"), ks.GraphIri)]);
        var result = await scope.ServiceProvider.GetRequiredService<RdfImportService>().ImportAsync(
            new(ks.Id, Encoding.UTF8.GetBytes($"<urn:instance> <{Vocabulary.RdfType}> <{ks.BaseIri}Pump> ."),
                "instance.nt", "abox", "merge", "ntriples", null), new Actor(seed.Actor.Id.ToString()), default);
        Assert.NotNull(result.Terminology);
        Assert.Null(result.Terminology.Error);
        Assert.Equal(1, result.Terminology.TermsAdded);
        await using var committed = await contexts.CreateDbContextAsync();
        Assert.NotEmpty(await new PostgresRdfStatementRepository(committed).ListAsync(ks.Id, "Vocabulary"));
        Assert.Single(await new PostgresRdfStatementRepository(committed).ListAsync(ks.Id, "ABox"));
        using var otherScope = services.CreateScope();
        Assert.NotSame(scope.ServiceProvider.GetRequiredService<ITerminologySync>(),
            otherScope.ServiceProvider.GetRequiredService<ITerminologySync>());
    }

    [Fact]
    public async Task Schema_add_class_preserves_non_target_graphs_in_both_layers()
    {
        await using var services = fixture.BuildServices();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var ks = await db.KnowledgeSystems.SingleAsync(item => item.Id == fixture.KnowledgeSystemId);
        var repository = new PostgresRdfStatementRepository(db);
        var otherSchema = new RdfStatement(new RdfIri("urn:schema:" + Guid.NewGuid()), Vocabulary.RdfType,
            new RdfIri(Vocabulary.OwlClass), "urn:other-schema");
        var otherFact = new RdfStatement(new RdfIri("urn:fact:" + Guid.NewGuid()), "urn:p",
            new RdfLiteral("retained"), "urn:other-facts");
        await repository.AppendIfAbsentAsync(ks.Id, "TBox", otherSchema);
        await repository.AppendIfAbsentAsync(ks.Id, "ABox", otherFact);
        var created = await new PostgresOntologyRepository(db, repository).ApplyEditAsync(ks.GraphIri, ks.BaseIri,
            "add_class", new Dictionary<string, object?> { ["label"] = "Schema " + Guid.NewGuid().ToString("N") });
        Assert.Contains(otherSchema, await repository.ListAsync(ks.Id, "TBox"));
        Assert.Contains(otherFact, await repository.ListAsync(ks.Id, "ABox"));
        Assert.Contains(await repository.ListAsync(ks.Id, "TBox"), item => item.Subject == new RdfIri(created)
            && item.GraphIri == ks.GraphIri.TrimEnd('/'));
    }

    [Fact]
    public async Task Post_graph_pre_provenance_failure_rolls_back_every_write_and_retry_commits()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var failure = new StatementSaveFailure(false);
        var injecting = await InjectingFactory(contexts, failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Push(injecting, secrets, seed));
        Assert.True(failure.SawGraphInTransaction);
        await using (var db = await contexts.CreateDbContextAsync())
        {
            Assert.False(await db.WorkspaceStatements.AnyAsync(item => item.Subject == seed.Input.Subject));
            Assert.False(await db.SourceStatements.AnyAsync(item => item.SourceId == seed.Source.Id));
            Assert.False(await db.SourceStatementFacts.AnyAsync(item => item.FactKey == failure.FactKey));
            Assert.False(await db.AuditEvents.AnyAsync(item => item.Action == "source.statements.push" && item.Detail!.RootElement.GetProperty("sourceId").GetString() == seed.Source.Id.ToString()));
        }
        Assert.Equal(200, (await Push(contexts, secrets, seed)).Status);
        Assert.Equal(0, (await Push(contexts, secrets, seed)).Associated);
    }

    [Fact]
    public async Task Actual_unique_violation_rolls_back_graph_then_recovers_under_fresh_locks()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var failure = new StatementSaveFailure(true);
        var injecting = await InjectingFactory(contexts, failure);
        var accepted = await Push(injecting, secrets, seed);
        Assert.True(failure.SawGraphInTransaction);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(0, (await Push(contexts, secrets, seed)).Associated);
        Assert.Equal(409, (await Push(contexts, secrets, seed with { Input = seed.Input with { Object = "conflict" } })).Status);
        await using var db = await contexts.CreateDbContextAsync();
        Assert.Single(await db.SourceStatements.Where(item => item.SourceId == seed.Source.Id).ToListAsync());
        Assert.Single(await db.SourceStatementFacts.Where(item => item.FactKey == accepted.FactKey).ToListAsync());
        Assert.Single(await db.WorkspaceStatements.Where(item => item.Subject == seed.Input.Subject).ToListAsync());
    }

    [Fact]
    public async Task Migration_metadata_provenance_query_snapshots_and_no_document_rows()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var first = await Seed(contexts, secrets);
        var second = (await Seed(contexts, secrets)) with { Input = first.Input };
        await using var db = await contexts.CreateDbContextAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20260930213301_AddSourceStatements", await db.Database.GetAppliedMigrationsAsync());
        var before = await DocumentCounts(db);
        var accepted = await Push(contexts, secrets, first);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(0, (await Push(contexts, secrets, second)).Added);
        Assert.Equal(before, await DocumentCounts(db));
        Assert.Contains((await new PostgresRdfStatementRepository(db).ListAsync(fixture.KnowledgeSystemId, "ABox")),
            item => item.Subject == new RdfIri(first.Input.Subject));
        Assert.Equal(204, (await Sources(db, secrets).DeleteAsync(fixture.KnowledgeSystemId, first.Source.Id, first.Actor, default)).StatusCode);
        var provenance = await new OntologyProvenanceService(db, new KnowledgeSystemAccessService())
            .GetProvenanceAsync(fixture.KnowledgeSystemId, new Actor(first.Actor.Id.ToString()), default);
        var group = Assert.Single(provenance!, item => item.AxiomKey == accepted.FactKey);
        Assert.Equal(2, group.Sources.Count);
        Assert.Contains(group.Sources, item => item.Method == "source" && item.Actor == first.Source.Name);
        var persisted = await db.SourceStatements.AsNoTracking().SingleAsync(item => item.Id == accepted.StatementId);
        Assert.Null(persisted.SourceId);
        Assert.Equal(first.Source.Name, persisted.SourceNameSnapshot);
        Assert.Empty((await new OntologyProvenanceService(db, new KnowledgeSystemAccessService())
            .GetProvenanceAsync(Guid.NewGuid(), new Actor(first.Actor.Id.ToString()), default)) ?? []);
        var indexes = await fixture.GetIndexDefinitionsAsync("source_statement", "source_statement_fact");
        Assert.Contains(indexes.Values, definition => definition.Contains("UNIQUE") && definition.Contains("ExternalStatementId") && definition.Contains("WHERE"));
        Assert.Contains(indexes.Values, definition => definition.Contains("UNIQUE") && definition.Contains("SourceStatementId") && definition.Contains("FactKey"));
        var recordModel = db.Model.FindEntityType(typeof(SourceStatementEntity))!;
        Assert.Equal(DeleteBehavior.SetNull, recordModel.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(SourceEntity)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, recordModel.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(KnowledgeSystemEntity)).DeleteBehavior);
    }

    [Theory]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("rotate", false)]
    [InlineData("rotate", true)]
    [InlineData("retry", false)]
    [InlineData("retry", true)]
    public async Task Source_lock_serializes_delete_rotation_and_same_id_retry(string operation, bool pushFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        await using var blocker = await fixture.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = barrier;
            command.CommandText = "SELECT id FROM source WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", seed.Source.Id);
            await command.ExecuteScalarAsync();
        }
        Task<SourceStatementResult> push;
        Task<int> mutation;
        try
        {
            if (pushFirst)
            {
                push = Push(contexts, secrets, seed);
                var pushPid = await WaitForWaiter(blocker.ProcessID, "source");
                mutation = Mutate(contexts, secrets, seed, operation);
                await WaitForWaiter(pushPid, "source");
            }
            else
            {
                mutation = Mutate(contexts, secrets, seed, operation);
                var mutationPid = await WaitForWaiter(blocker.ProcessID, "source");
                push = Push(contexts, secrets, seed);
                await WaitForWaiter(mutationPid, "source");
            }
        }
        finally { await barrier.CommitAsync(); }
        Assert.Equal(operation == "delete" ? 204 : 200, await mutation.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(!pushFirst && operation != "retry" ? 401 : 200, (await push.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        await using var db = await contexts.CreateDbContextAsync();
        var expected = pushFirst || operation == "retry" ? 1 : 0;
        Assert.Equal(expected, await db.WorkspaceStatements.CountAsync(item => item.Subject == seed.Input.Subject));
        Assert.Equal(expected, await db.SourceStatements.CountAsync(item => item.ExternalStatementId == seed.Input.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_writer_and_push_lock_before_old_layer_read_and_preserve_other_graph(bool pushFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seed = await Seed(contexts, secrets);
        var other = new RdfStatement(new RdfBlankNode("other-" + Guid.NewGuid().ToString("N")), "urn:p", new RdfLiteral("other"), "urn:other-graph");
        await using (var db = await contexts.CreateDbContextAsync())
            await new PostgresRdfStatementRepository(db).AppendIfAbsentAsync(fixture.KnowledgeSystemId, "ABox", other);
        await using var blocker = await fixture.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = barrier;
            command.CommandText = "SELECT id FROM knowledgesystem WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", fixture.KnowledgeSystemId);
            await command.ExecuteScalarAsync();
        }
        Task<SourceStatementResult> push;
        Task<string> manual;
        try
        {
            if (pushFirst)
            {
                push = Push(contexts, secrets, seed);
                var pushPid = await WaitForWaiter(blocker.ProcessID, "knowledgesystem");
                manual = Task.Run(() => Manual(contexts));
                await WaitForWaiter(pushPid, "knowledgesystem");
            }
            else
            {
                manual = Task.Run(() => Manual(contexts));
                var manualPid = await WaitForWaiter(blocker.ProcessID, "knowledgesystem");
                push = Push(contexts, secrets, seed);
                await WaitForWaiter(manualPid, "knowledgesystem");
            }
        }
        finally { await barrier.CommitAsync(); }
        Assert.Equal(200, (await push.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        var individual = await manual.WaitAsync(TimeSpan.FromSeconds(15));
        await using var state = await contexts.CreateDbContextAsync();
        var facts = await new PostgresRdfStatementRepository(state).ListAsync(fixture.KnowledgeSystemId, "ABox");
        Assert.Contains(other, facts);
        Assert.Contains(facts, item => item.Subject == new RdfIri(seed.Input.Subject));
        Assert.Contains(facts, item => item.Subject == new RdfIri(individual));
    }

    private async Task<string> Manual(IDbContextFactory<ISEStudioDbContext> contexts)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var ks = await db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == fixture.KnowledgeSystemId);
        return new ABoxManager(new PostgresRdfStatementRepository(db)).CreateIndividual(KsContext.FromEntity(ks), "manual", "urn:class", "Manual");
    }

    private async Task<int> Mutate(IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets, Seeded seed, string operation)
    {
        if (operation == "retry") return (await Push(contexts, secrets, seed)).Status;
        await using var db = await contexts.CreateDbContextAsync();
        var sources = Sources(db, secrets);
        return operation == "delete"
            ? (await sources.DeleteAsync(fixture.KnowledgeSystemId, seed.Source.Id, seed.Actor, default)).StatusCode
            : (await sources.RotateTokenAsync(fixture.KnowledgeSystemId, seed.Source.Id, seed.Actor, default)).StatusCode;
    }

    private async Task<int> WaitForWaiter(int blockerPid, string table)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var connection = await fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND state = 'active' AND wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid)) AND query ILIKE @query ORDER BY query_start LIMIT 1";
            command.Parameters.AddWithValue("blocker", blockerPid);
            command.Parameters.AddWithValue("query", "%" + table + "%FOR%UPDATE%");
            if (await command.ExecuteScalarAsync() is int pid) return pid;
            await Task.Delay(20);
        }
        throw new TimeoutException("Statement writer did not block before reading old state.");
    }

    private async Task<Seeded> Seed(IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var actor = new UserEntity { Username = "statement-" + Guid.NewGuid().ToString("N"), IsAdmin = true, Active = true, PasswordHash = "unused", CreatedAt = DateTimeOffset.UtcNow };
        var source = new SourceEntity { KnowledgeSystemId = fixture.KnowledgeSystemId, Kind = "statements", Name = "statement-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var token = "statement-" + Guid.NewGuid().ToString("N");
        source.IngestTokenCiphertext = secrets.Seal(token, $"{fixture.KnowledgeSystemId:D}:{source.Id:D}:token");
        db.Users.Add(actor);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return new(source, actor, token, new("item-" + Guid.NewGuid().ToString("N"), "urn:statement:" + Guid.NewGuid(), "urn:p", "hello", "literal"));
    }

    private async Task<SourceStatementResult> Push(IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets, Seeded seed)
    {
        await using var db = await contexts.CreateDbContextAsync();
        return await new SourceStatementService(contexts, Sources(db, secrets), TimeProvider.System).PushAsync(fixture.KnowledgeSystemId, seed.Source.Id, seed.Token, seed.Input, default);
    }

    private static SourceService Sources(ISEStudioDbContext db, ISourceSecretProtector secrets)
        => new(db, new KnowledgeSystemAccessService(), new SourceAdapterRegistry([new SourceKindDescriptor("statements", false, [])]), secrets, TimeProvider.System);

    private static SourceSecretProtector Secrets() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());

    private static async Task<int[]> DocumentCounts(ISEStudioDbContext db)
        => [await db.Documents.CountAsync(), await db.Chunks.CountAsync(), await db.DocumentFileVersions.CountAsync(), await db.DocumentParseJobs.CountAsync()];

    private static async Task<IDbContextFactory<ISEStudioDbContext>> InjectingFactory(IDbContextFactory<ISEStudioDbContext> contexts, SaveChangesInterceptor interceptor)
    {
        await using var db = await contexts.CreateDbContextAsync();
        return new ContextFactory(new DbContextOptionsBuilder<ISEStudioDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
    }

    private sealed record Seeded(SourceEntity Source, UserEntity Actor, string Token, SourceStatementRequest Input);

    private sealed class ContextFactory(DbContextOptions<ISEStudioDbContext> options) : IDbContextFactory<ISEStudioDbContext>
    {
        public ISEStudioDbContext CreateDbContext() => new(options);
        public Task<ISEStudioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class DocumentAuditBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource<int> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (ISEStudioDbContext)eventData.Context!;
            if (db.ChangeTracker.Entries<AuditEventEntity>().Any(item => item.State == EntityState.Added && item.Entity.Action == "document.delete"))
            {
                Reached.TrySetResult(((Npgsql.NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class BatchSaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SourceStatementEntity>().Any(item =>
                item.State == EntityState.Added && item.Entity.ExternalStatementId == "fault"))
                throw new DbUpdateException("SENSITIVE-BODY token=SECRET");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StatementSaveFailure(bool uniqueConflict) : SaveChangesInterceptor
    {
        private bool _fired;
        public bool SawGraphInTransaction { get; private set; }
        public string? FactKey { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (ISEStudioDbContext)eventData.Context!;
            var entry = db.ChangeTracker.Entries<SourceStatementEntity>().FirstOrDefault(item => item.State == EntityState.Added);
            if (_fired || entry is null) return result;
            _fired = true;
            FactKey = entry.Entity.FactKey;
            SawGraphInTransaction = db.Database.CurrentTransaction is not null && await db.WorkspaceStatements.AnyAsync(cancellationToken);
            Assert.True(SawGraphInTransaction);
            if (!uniqueConflict) throw new InvalidOperationException("Injected provenance save failure");
            var original = entry.Entity;
            db.SourceStatements.Add(new SourceStatementEntity { KnowledgeSystemId = original.KnowledgeSystemId, SourceId = original.SourceId,
                ExternalStatementId = original.ExternalStatementId, PayloadSha256 = original.PayloadSha256, FactKey = original.FactKey,
                SourceNameSnapshot = original.SourceNameSnapshot, CreatedAt = original.CreatedAt });
            return result;
        }
    }
}