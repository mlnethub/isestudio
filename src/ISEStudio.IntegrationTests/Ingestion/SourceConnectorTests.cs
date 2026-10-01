using System.Text;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using ISEStudio.Sources;
using ISEStudio.Sources.Adapters;
using ISEStudio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Authorization;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using ISEStudio.Ontology;

namespace ISEStudio.IntegrationTests.Ingestion;

[Collection(SourceSyncTestCollection.Name)]
public sealed class SourceConnectorTests(PostgresGraphFixture fixture) : IClassFixture<PostgresGraphFixture>
{
    [Fact]
    public async Task Task7_statements_append_idempotency_multiple_sources_and_deletion_snapshot()
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var first = await SeedStatement(contexts, secrets);
        var second = await SeedStatement(contexts, secrets);
        var input = Statement();
        var accepted = await StatementPush(contexts, secrets, first.Source.Id, first.Token, input);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(1, accepted.Added);
        var retry = await StatementPush(contexts, secrets, first.Source.Id, first.Token, input with { Language = "en" });
        Assert.Equal(accepted.StatementId, retry.StatementId);
        Assert.Equal(0, retry.Associated);
        Assert.Equal(409, (await StatementPush(contexts, secrets, first.Source.Id, first.Token, input with { Object = "changed" })).Status);
        var duplicate = await StatementPush(contexts, secrets, second.Source.Id, second.Token, input);
        Assert.Equal(0, duplicate.Added);
        Assert.Equal(1, duplicate.Associated);
        Assert.Equal(204, (await DeletePushSource(contexts, secrets, first.Source.Id, first.Actor.Id)).StatusCode);
        await using var db = await contexts.CreateDbContextAsync();
        var records = await db.SourceStatements.Where(item => item.FactKey == accepted.FactKey).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(first.Source.Name, records.Single(item => item.SourceId == null).SourceNameSnapshot);
        Assert.Equal(2, await db.SourceStatementFacts.CountAsync(item => item.FactKey == accepted.FactKey));
        Assert.Single((await new PostgresRdfStatementRepository(db).ListAsync(fixture.KnowledgeSystemId, "ABox"))
            .Where(item => item.Subject == new RdfIri(input.Subject)));
    }

    [Fact]
    public async Task Task7_manual_edit_preserves_unrelated_current_and_other_graph_statements()
    {
        await using var services = fixture.BuildServices();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var repository = new PostgresRdfStatementRepository(db);
        var ks = await db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == fixture.KnowledgeSystemId);
        var context = KsContext.FromEntity(ks);
        var other = new RdfStatement(new RdfIri("urn:task7:" + Guid.NewGuid()), "urn:p", new RdfLiteral("other"), "urn:other-graph");
        await repository.AppendIfAbsentAsync(ks.Id, "ABox", other);
        var manager = new ABoxManager(repository);
        var individual = manager.CreateIndividual(context, "manual", "urn:class", "Manual");
        Assert.Contains(other, await repository.ListAsync(ks.Id, "ABox"));
        manager.DeleteIndividual(context, individual);
        Assert.Contains(other, await repository.ListAsync(ks.Id, "ABox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task7_push_and_merge_lock_before_reading_and_preserve_both_commits(bool pushFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var seeded = await SeedStatement(contexts, secrets);
        var input = Statement();
        var mergedSubject = "urn:merge:" + Guid.NewGuid();
        await using var blocker = await fixture.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = barrier;
            command.CommandText = "SELECT id FROM knowledgesystem WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", fixture.KnowledgeSystemId);
            await command.ExecuteScalarAsync();
        }
        Task merge;
        Task<SourceStatementResult> push;
        try
        {
            if (pushFirst)
            {
                push = StatementPush(contexts, secrets, seeded.Source.Id, seeded.Token, input);
                var pushPid = await WaitForKsWaiter(blocker.ProcessID);
                merge = MergeStatement(contexts, mergedSubject);
                await WaitForKsWaiter(pushPid);
            }
            else
            {
                merge = MergeStatement(contexts, mergedSubject);
                var mergePid = await WaitForKsWaiter(blocker.ProcessID);
                push = StatementPush(contexts, secrets, seeded.Source.Id, seeded.Token, input);
                await WaitForKsWaiter(mergePid);
            }
        }
        finally { await barrier.CommitAsync(); }
        Assert.Equal(200, (await push.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        await merge.WaitAsync(TimeSpan.FromSeconds(15));
        await using var state = await contexts.CreateDbContextAsync();
        var facts = await new PostgresRdfStatementRepository(state).ListAsync(fixture.KnowledgeSystemId, "ABox");
        Assert.Contains(facts, item => item.Subject == new RdfIri(input.Subject));
        Assert.Contains(facts, item => item.Subject == new RdfIri(mergedSubject));
    }

    private async Task MergeStatement(IDbContextFactory<ISEStudioDbContext> contexts, string subject)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var ks = await db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == fixture.KnowledgeSystemId);
        await new RdfImportService(new PostgresRdfStatementRepository(db), new RdfImportParser()).ImportAsync(
            KsContext.FromEntity(ks), RdfLayer.ABox, Encoding.UTF8.GetBytes($"<{subject}> <urn:p> \"merge\" <urn:ignored> ."), ImportMode.Merge);
    }

    private async Task<int> WaitForKsWaiter(int blockerPid)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var connection = await fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND state = 'active' AND wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid)) AND query ILIKE '%knowledgesystem%FOR%UPDATE%' ORDER BY query_start LIMIT 1";
            command.Parameters.AddWithValue("blocker", blockerPid);
            if (await command.ExecuteScalarAsync() is int pid) return pid;
            await Task.Delay(20);
        }
        throw new TimeoutException("Task7 KS lock waiter did not appear before the old layer was read.");
    }

    private async Task<(SourceEntity Source, UserEntity Actor, string Token)> SeedStatement(
        IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets)
    {
        var result = await SeedPush(contexts, secrets);
        await using var db = await contexts.CreateDbContextAsync();
        var source = await db.Sources.SingleAsync(item => item.Id == result.Source.Id);
        source.Kind = "statements";
        await db.SaveChangesAsync();
        result.Source.Kind = source.Kind;
        return result;
    }

    private static SourceStatementRequest Statement() => new("upstream-1", "urn:task7:" + Guid.NewGuid(), "urn:predicate", "hello", "literal", Language: "EN");

    private async Task<SourceStatementResult> StatementPush(IDbContextFactory<ISEStudioDbContext> contexts,
        ISourceSecretProtector secrets, Guid sourceId, string token, SourceStatementRequest input)
    {
        await using var db = await contexts.CreateDbContextAsync();
        return await new SourceStatementService(contexts, PushSources(db, secrets), TimeProvider.System)
            .PushAsync(fixture.KnowledgeSystemId, sourceId, token, input, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task6_push_and_real_source_delete_follow_source_then_document_lock_order(bool pushFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var blobs = new PushCountingBlobs(services.GetRequiredService<IBlobStore>());
        var (source, actor, token) = await SeedPush(contexts, secrets);
        if (pushFirst)
            Assert.Equal(200, (await Push(contexts, blobs, secrets, source.Id, token, "before")).StatusCode);
        var writesBefore = blobs.Writes;

        await using var blocker = await fixture.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = barrier;
            command.CommandText = "SELECT id FROM source WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", source.Id);
            await command.ExecuteScalarAsync();
        }
        Task<SourceMutationResult<SourceItemResult>> push;
        Task<SourceMutationResult<bool>> deletion;
        try
        {
            if (pushFirst)
            {
                push = Push(contexts, blobs, secrets, source.Id, token, "after");
                var pushPid = await WaitForPushLockWaiter(blocker.ProcessID, source.Id, false);
                deletion = DeletePushSource(contexts, secrets, source.Id, actor.Id);
                await WaitForPushLockWaiter(pushPid, source.Id, true);
            }
            else
            {
                deletion = DeletePushSource(contexts, secrets, source.Id, actor.Id);
                var deletePid = await WaitForPushLockWaiter(blocker.ProcessID, source.Id, true);
                push = Push(contexts, blobs, secrets, source.Id, token, "after");
                await WaitForPushLockWaiter(deletePid, source.Id, false);
            }
        }
        finally { await barrier.CommitAsync(); }
        var pushed = await push.WaitAsync(TimeSpan.FromSeconds(15));
        var deleted = await deletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(204, deleted.StatusCode);
        Assert.Equal(pushFirst ? 200 : 401, pushed.StatusCode);
        await using var state = await contexts.CreateDbContextAsync();
        Assert.False(await state.Sources.AnyAsync(item => item.Id == source.Id));
        Assert.False(await state.SourceDocumentBindings.AnyAsync(item => item.SourceId == source.Id));
        Assert.False(await state.SourceSyncRuns.AnyAsync(item => item.SourceId == source.Id));
        var sha = PushHash(source.Id, "after");
        var documents = await state.Documents.Where(item => item.Sha256 == sha).ToListAsync();
        if (pushFirst)
        {
            var document = Assert.Single(documents);
            Assert.Null(document.SourceId);
            Assert.Null(document.ExternalKey);
            Assert.Null(document.MissingSince);
            Assert.Equal(2, await state.DocumentFileVersions.CountAsync(item => item.DocumentId == document.Id));
            Assert.Equal(2, await state.DocumentParseJobs.CountAsync(item => item.DocumentId == document.Id));
            Assert.True(await blobs.ExistsAsync(sha, default));
            Assert.Equal(writesBefore + 1, blobs.Writes);
        }
        else
        {
            Assert.Empty(documents);
            Assert.False(await blobs.ExistsAsync(sha, default));
            Assert.Equal(writesBefore, blobs.Writes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task6_token_rotation_is_rechecked_under_the_same_source_write_lock(bool pushFirst)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        var blobs = new PushCountingBlobs(services.GetRequiredService<IBlobStore>());
        var (source, actor, token) = await SeedPush(contexts, secrets);
        await using var blocker = await fixture.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = barrier;
            command.CommandText = "SELECT id FROM source WHERE id = @id FOR UPDATE";
            command.Parameters.AddWithValue("id", source.Id);
            await command.ExecuteScalarAsync();
        }
        Task<SourceMutationResult<SourceItemResult>> push;
        Task<SourceMutationResult<SourceTokenOut>> rotation;
        try
        {
            if (pushFirst)
            {
                push = Push(contexts, blobs, secrets, source.Id, token, "race");
                var pushPid = await WaitForPushLockWaiter(blocker.ProcessID, source.Id, false);
                rotation = RotatePushToken(contexts, secrets, source.Id, actor.Id);
                await WaitForPushLockWaiter(pushPid, source.Id, true);
            }
            else
            {
                rotation = RotatePushToken(contexts, secrets, source.Id, actor.Id);
                var rotationPid = await WaitForPushLockWaiter(blocker.ProcessID, source.Id, true);
                push = Push(contexts, blobs, secrets, source.Id, token, "race");
                await WaitForPushLockWaiter(rotationPid, source.Id, false);
            }
        }
        finally { await barrier.CommitAsync(); }
        Assert.Equal(pushFirst ? 200 : 401, (await push.WaitAsync(TimeSpan.FromSeconds(15))).StatusCode);
        var rotated = await rotation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(200, rotated.StatusCode);
        Assert.Equal(pushFirst ? 1 : 0, blobs.Writes);
        Assert.Equal(401, (await Push(contexts, blobs, secrets, source.Id, token, "old")).StatusCode);
        Assert.False(await blobs.ExistsAsync(PushHash(source.Id, "old"), default));
        Assert.Equal(200, (await Push(contexts, blobs, secrets, source.Id, rotated.Value!.Token, "new")).StatusCode);
        await using var state = await contexts.CreateDbContextAsync();
        Assert.False(await state.SourceSyncRuns.AnyAsync(item => item.SourceId == source.Id));
        Assert.False(await state.SourceSyncJobs.AnyAsync(item => item.SourceId == source.Id));
    }

    private async Task<(SourceEntity Source, UserEntity Actor, string Token)> SeedPush(
        IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var actor = new UserEntity { Id = Guid.NewGuid(), Username = "task6-" + Guid.NewGuid().ToString("N"), PasswordHash = "unused", IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow };
        var source = new SourceEntity { Id = Guid.NewGuid(), KnowledgeSystemId = fixture.KnowledgeSystemId, Kind = "api", Name = "task6-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var token = "test-" + Guid.NewGuid().ToString("N");
        source.IngestTokenCiphertext = secrets.Seal(token, $"{source.KnowledgeSystemId:D}:{source.Id:D}:token");
        db.Users.Add(actor);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return (source, actor, token);
    }

    private static SourceService PushSources(ISEStudioDbContext db, ISourceSecretProtector secrets)
        => new(db, new KnowledgeSystemAccessService(), new SourceAdapterRegistry([
            new SourceKindDescriptor("api", false, [])]), secrets, TimeProvider.System);

    private async Task<SourceMutationResult<SourceItemResult>> Push(
        IDbContextFactory<ISEStudioDbContext> contexts, IBlobStore blobs, ISourceSecretProtector secrets,
        Guid sourceId, string token, string content)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var coordinator = new SourceSyncCoordinator(contexts, blobs, [], new SourceSyncJobStore(contexts, TimeProvider.System), TimeProvider.System);
        var pushes = new SourcePushService(PushSources(db, secrets), coordinator);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;
        context.Request.Headers["X-Source-Filename"] = "report.txt";
        context.Request.ContentType = "text/plain";
        context.Request.QueryString = new QueryString("?external_key=upstream-id");
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes($"{sourceId:D}:{content}"));
        context.Request.Body = body;
        return await pushes.PushDocumentAsync(fixture.KnowledgeSystemId, sourceId, context.Request, default);
    }

    private async Task<SourceMutationResult<bool>> DeletePushSource(
        IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets, Guid sourceId, Guid actorId)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var actor = await db.Users.SingleAsync(item => item.Id == actorId);
        return await PushSources(db, secrets).DeleteAsync(fixture.KnowledgeSystemId, sourceId, actor, default);
    }

    private async Task<SourceMutationResult<SourceTokenOut>> RotatePushToken(
        IDbContextFactory<ISEStudioDbContext> contexts, ISourceSecretProtector secrets, Guid sourceId, Guid actorId)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var actor = await db.Users.SingleAsync(item => item.Id == actorId);
        return await PushSources(db, secrets).RotateTokenAsync(fixture.KnowledgeSystemId, sourceId, actor, default);
    }

    private async Task<int> WaitForPushLockWaiter(int blockerPid, Guid sourceId, bool management)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var connection = await fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pid FROM pg_stat_activity
                WHERE pid <> pg_backend_pid() AND datname = current_database()
                  AND state = 'active' AND wait_event_type = 'Lock'
                  AND @blocker = ANY(pg_blocking_pids(pid)) AND query ILIKE '%FOR UPDATE%'
                  AND ((@management AND query ILIKE '%AND knowledge_system_id =%')
                    OR (NOT @management AND query NOT ILIKE '%AND knowledge_system_id =%'))
                ORDER BY query_start LIMIT 1
                """;
            command.Parameters.AddWithValue("blocker", blockerPid);
            command.Parameters.AddWithValue("management", management);
            if (await command.ExecuteScalarAsync() is int pid) return pid;
            await Task.Delay(20);
        }
        throw new TimeoutException("Task6 Source lock waiter did not appear.");
    }

    private static string PushHash(Guid sourceId, string content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:D}:{content}"))).ToLowerInvariant();

    private sealed class PushCountingBlobs(IBlobStore inner) : IBlobStore
    {
        public int Writes { get; private set; }
        public Task<BlobWriteResult> PutAsync(Stream stream, CancellationToken ct) { Writes++; return inner.PutAsync(stream, ct); }
        public Task<Stream?> GetAsync(string sha, CancellationToken ct) => inner.GetAsync(sha, ct);
        public Task<bool> ExistsAsync(string sha, CancellationToken ct) => inner.ExistsAsync(sha, ct);
        public Task<bool> RemoveAsync(string sha, CancellationToken ct) => inner.RemoveAsync(sha, ct);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Object_identity_retry_and_fresh_content_pin_file_versions_and_parse_jobs(string kind)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var sdk = new FakeSdk();
        var harness = await Create(services, secrets, sdk, kind);
        sdk.Objects = [harness.Object("docs/a/report.txt", "alpha"), harness.Object("docs/b/report.txt", "beta")];
        var first = await harness.Run();
        Assert.Equal(2, first.Added);
        Assert.True(first.IsComplete);
        Assert.Empty(first.Errors);
        var original = await harness.State();
        Assert.Equal(2, original.Documents.Count);
        Assert.Equal(2, original.Versions.Count);
        Assert.Equal(2, original.Jobs.Count);
        Assert.All(original.Versions, version => Assert.Equal(harness.Timestamp, version.DocTime));
        Assert.All(original.Jobs, job => Assert.Equal(original.Versions.Single(version => version.Id == job.DocumentFileVersionId).Sha256, job.Sha256));
        Assert.Equal(2, original.Bindings.Select(binding => binding.ExternalKey).Distinct().Count());
        Assert.All(original.Documents, document => Assert.Equal("report.txt", document.OriginalFilename));
        var firstA = original.Bindings.Single(binding => binding.ExternalKey.EndsWith("/docs/a/report.txt", StringComparison.Ordinal));

        var retry = await harness.Run();
        Assert.Equal(0, retry.Added);
        Assert.Equal(0, retry.Updated);
        Assert.Equal(2, (await harness.State()).Versions.Count);
        sdk.Objects = [harness.Object("docs/a/report.txt", "changed", "2"), harness.Object("docs/b/report.txt", "beta")];
        var changed = await harness.Run();
        Assert.Equal(1, changed.Updated);
        var updated = await harness.State();
        Assert.Equal(firstA.DocumentId, updated.Bindings.Single(binding => binding.ExternalKey == firstA.ExternalKey).DocumentId);
        Assert.Equal([1, 2], updated.Versions.Where(version => version.DocumentId == firstA.DocumentId).OrderBy(version => version.Version).Select(version => version.Version));
        Assert.Equal(3, updated.Jobs.Count);
        var latest = updated.Versions.Single(version => version.DocumentId == firstA.DocumentId && version.Version == 2);
        var parse = Assert.Single(updated.Jobs, job => job.DocumentFileVersionId == latest.Id);
        Assert.Equal(latest.Sha256, parse.Sha256);
        Assert.Equal(["1", "1", "1", "1", "2", "1"], sdk.DownloadedVersions);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Interrupted_pagination_commits_prior_items_without_marking_previous_objects_missing(string kind)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var sdk = new FakeSdk();
        var harness = await Create(services, secrets, sdk, kind);
        sdk.Objects = [harness.Object("docs/a.txt", "alpha"), harness.Object("docs/b.txt", "beta")];
        await harness.Run();
        var baseline = await harness.State();
        sdk.Objects = [harness.Object("docs/a.txt", "changed", "2"), harness.Object("docs/c.txt", "gamma")];
        sdk.InterruptNextPage = true;
        var result = await harness.Run();
        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.DoesNotContain("SECRET", string.Join(" ", result.Errors));
        var partial = await harness.State();
        Assert.Equal(3, partial.Documents.Count);
        Assert.Equal(4, partial.Versions.Count);
        Assert.Equal(4, partial.Jobs.Count);
        Assert.All(partial.Bindings, binding => Assert.Null(binding.MissingSince));
        Assert.All(partial.Documents, document => Assert.Null(document.MissingSince));
        Assert.Equal("failed", partial.Source.LastSyncStatus);
        Assert.Equal("failed", partial.Run.Status);
        foreach (var version in baseline.Versions) Assert.Contains(partial.Versions, current => current.Id == version.Id);
        foreach (var job in baseline.Jobs) Assert.Contains(partial.Jobs, current => current.Id == job.Id);
        foreach (var version in partial.Versions)
        {
            await using var stream = await services.GetRequiredService<IBlobStore>().GetAsync(version.Sha256, default);
            Assert.NotNull(stream);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            Assert.True(bytes.Length > 0);
            Assert.Equal(version.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }

        sdk.InterruptNextPage = false;
        var complete = await harness.Run();
        Assert.True(complete.IsComplete);
        Assert.Empty(complete.Errors);
        var reconciled = await harness.State();
        Assert.NotNull(reconciled.Bindings.Single(binding => binding.ExternalKey.EndsWith("/docs/b.txt", StringComparison.Ordinal)).MissingSince);
        Assert.Equal(3, reconciled.Documents.Count);
        Assert.Equal(4, reconciled.Versions.Count);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Download_failure_keeps_successful_later_objects_and_prior_document_versions(string kind)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var sdk = new FakeSdk();
        var harness = await Create(services, secrets, sdk, kind);
        sdk.Objects = [harness.Object("docs/a.txt", "alpha"), harness.Object("docs/b.txt", "beta")];
        await harness.Run();
        var baseline = await harness.State();
        sdk.Objects = [harness.Object("docs/a.txt", "changed", "2"), harness.Object("docs/b.txt", "failed"), harness.Object("docs/c.txt", "gamma")];
        sdk.FailedDownload = "docs/b.txt";
        var partial = await harness.Run();
        Assert.False(partial.IsComplete);
        Assert.Equal(1, partial.Added);
        Assert.Equal(1, partial.Updated);
        var state = await harness.State();
        Assert.Equal(3, state.Documents.Count);
        Assert.Equal(4, state.Versions.Count);
        Assert.Equal(4, state.Jobs.Count);
        Assert.All(state.Bindings, binding => Assert.Null(binding.MissingSince));
        Assert.All(state.Documents, document => Assert.Null(document.MissingSince));
        var originalB = baseline.Bindings.Single(binding => binding.ExternalKey.EndsWith("/docs/b.txt", StringComparison.Ordinal));
        Assert.Equal(baseline.Documents.Single(document => document.Id == originalB.DocumentId).Sha256,
            state.Documents.Single(document => document.Id == originalB.DocumentId).Sha256);
        Assert.Equal("failed", state.Run.Status);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("gcs")]
    public async Task Successful_empty_bucket_marks_missing_without_deleting_prior_objects(string kind)
    {
        await using var services = fixture.BuildServices();
        using var secrets = Secrets();
        var sdk = new FakeSdk();
        var harness = await Create(services, secrets, sdk, kind);
        sdk.Objects = [harness.Object("docs/a.txt", "alpha")];
        await harness.Run();
        var baseline = await harness.State();
        sdk.Objects = [];
        var empty = await harness.Run();
        Assert.True(empty.IsComplete);
        Assert.Empty(empty.Errors);
        var state = await harness.State();
        Assert.Equal(baseline.Documents.Single().Id, state.Documents.Single().Id);
        Assert.NotNull(state.Documents.Single().MissingSince);
        Assert.NotNull(state.Bindings.Single().MissingSince);
        Assert.Equal(baseline.Versions.Single().Id, state.Versions.Single().Id);
        Assert.Equal(baseline.Jobs.Single().Id, state.Jobs.Single().Id);
        await using var retained = await services.GetRequiredService<IBlobStore>().GetAsync(baseline.Versions.Single().Sha256, default);
        Assert.NotNull(retained);
        using var reader = new StreamReader(retained);
        Assert.EndsWith(":alpha", await reader.ReadToEndAsync());
        Assert.Equal("ok", state.Run.Status);
    }

    private static SourceSecretProtector Secrets() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { [SourceSecretProtector.ConfigurationKey] = Convert.ToBase64String(new byte[32]) }).Build());

    private async Task<Harness> Create(ServiceProvider services, ISourceSecretProtector secrets, FakeSdk sdk, string kind)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var source = new SourceEntity
        {
            Id = Guid.NewGuid(), KnowledgeSystemId = fixture.KnowledgeSystemId, Kind = kind,
            Name = $"task4-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow,
        };
        var config = new Dictionary<string, string> { ["bucket"] = "test-bucket", ["prefix"] = "docs/" };
        if (kind == "s3")
        {
            config["region"] = "us-east-1";
            config["access_key_id"] = "test-access";
            config["secret_access_key"] = "test-secret";
        }
        else
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            config["service_account_key"] = JsonSerializer.Serialize(new
            {
                type = "service_account", client_email = "source@test.iam.gserviceaccount.com",
                private_key = key.ExportPkcs8PrivateKeyPem(), token_uri = "https://oauth2.googleapis.com/token",
            });
        }
        foreach (var field in ObjectStorageSourceConfig.Descriptors.Single(item => item.Kind == kind).ConfigFields.Where(field => field.Secret))
            if (config.TryGetValue(field.Name, out var value))
                config[field.Name] = secrets.Seal(value, $"{source.KnowledgeSystemId:D}:{source.Id:D}:{field.Name}");
        source.Config = JsonSerializer.Serialize(config);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        ISourceAdapter adapter = kind == "s3" ? new S3SourceAdapter(sdk, secrets, TimeProvider.System) : new GcsSourceAdapter(sdk, secrets, TimeProvider.System);
        var jobs = new SourceSyncJobStore(contexts, TimeProvider.System);
        return new Harness(source.Id, sdk, contexts, jobs,
            new SourceSyncCoordinator(contexts, services.GetRequiredService<IBlobStore>(), [adapter], jobs, TimeProvider.System));
    }

    private sealed class Harness(Guid sourceId, FakeSdk sdk, IDbContextFactory<ISEStudioDbContext> contexts,
        SourceSyncJobStore jobs, SourceSyncCoordinator coordinator)
    {
        private Guid _runId;
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.Parse("2024-02-03T04:05:06Z");
        public ObjectStorageObject Object(string key, string content, string version = "1")
        {
            sdk.Contents[key] = Encoding.UTF8.GetBytes($"{sourceId:D}:{content}");
            return new(key, sdk.Contents[key].Length, version, Timestamp);
        }
        public async Task<SourceSyncResult> Run()
        {
            var id = await jobs.EnqueueAsync(sourceId, default);
            var claim = Assert.IsType<SourceSyncJobEntity>(await jobs.ClaimNextAsync(default));
            Assert.Equal(sourceId, claim.SourceId);
            _runId = Assert.IsType<Guid>(claim.ActiveRunId);
            var result = await coordinator.RunAsync(sourceId, id, _runId, default);
            await jobs.CompleteAsync(id, _runId, result, default);
            return result;
        }
        public async Task<SourceState> State()
        {
            await using var db = await contexts.CreateDbContextAsync();
            var bindings = await db.SourceDocumentBindings.AsNoTracking().Where(item => item.SourceId == sourceId).ToListAsync();
            var ids = bindings.Select(binding => binding.DocumentId).ToArray();
            return new(await db.Sources.AsNoTracking().SingleAsync(item => item.Id == sourceId),
                await db.SourceSyncRuns.AsNoTracking().SingleAsync(item => item.Id == _runId), bindings,
                await db.Documents.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(),
                await db.DocumentFileVersions.AsNoTracking().Where(item => ids.Contains(item.DocumentId)).ToListAsync(),
                await db.DocumentParseJobs.AsNoTracking().Where(item => ids.Contains(item.DocumentId)).ToListAsync());
        }
    }

    private sealed record SourceState(SourceEntity Source, SourceSyncRunEntity Run,
        List<SourceDocumentBindingEntity> Bindings, List<DocumentEntity> Documents,
        List<DocumentFileVersionEntity> Versions, List<DocumentParseJobEntity> Jobs);

    private sealed class FakeSdk : IS3SourceClientFactory, IGcsSourceClientFactory, IObjectStorageSourceClient
    {
        public IReadOnlyList<ObjectStorageObject> Objects { get; set; } = [];
        public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);
        public bool InterruptNextPage { get; set; }
        public string? FailedDownload { get; set; }
        public List<string?> DownloadedVersions { get; } = [];
        public IObjectStorageSourceClient Create(S3SourceCredentials credentials) => this;
        public IObjectStorageSourceClient Create(GcsSourceCredentials credentials) => this;
        public Task<ObjectStoragePage> ListAsync(string bucket, string prefix, string? token, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (token is not null) throw new IOException("SECRET pagination response");
            return Task.FromResult(new ObjectStoragePage(Objects, InterruptNextPage ? "next" : null));
        }
        public async Task<string?> DownloadAsync(string bucket, ObjectStorageObject item, Stream destination, CancellationToken ct)
        {
            if (item.Key == FailedDownload) throw new IOException("SECRET download response");
            DownloadedVersions.Add(item.Version);
            await destination.WriteAsync(Contents[item.Key], ct);
            return "text/plain";
        }
        public void Dispose() { }
    }
}