using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Extraction;

public sealed class Task7RdfWriterScopeTests(PostgresRdfFixture fixture) : IClassFixture<PostgresRdfFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABox_capture_nested_merger_same_transaction_preserves_per_chunk_visibility_and_rollback(bool failAfterMerge)
    {
        var ks = KsContext.FromEntity(await fixture.Db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == fixture.KnowledgeSystemId));
        var contexts = new ContextFactory(new DbContextOptionsBuilder<ISEStudioDbContext>()
            .UseNpgsql(fixture.Db.Database.GetConnectionString()).Options);
        fixture.TBox.AddStatements(ks.TBoxGraph, [
            new(new RdfIri(ks.BaseIri + "Person"), Vocabulary.RdfType, new RdfIri(Vocabulary.OwlClass), ks.TBoxGraph),
            new(new RdfIri(ks.BaseIri + "Person"), Vocabulary.RdfsLabel, new RdfLiteral("Person"), ks.TBoxGraph)]);
        var other = new RdfStatement(new RdfIri("urn:other"), "urn:p", new RdfLiteral("other"), "urn:other-graph");
        await fixture.Statements.AppendIfAbsentAsync(ks.KnowledgeSystemId, "ABox", other);
        var jobs = new ExtractionJobStore(contexts, TimeProvider.System);
        var job = await jobs.CreateAsync(ks.KnowledgeSystemId, "abox", "fake", [1, 2], 2, default);
        var root = Path.Combine(Path.GetTempPath(), "task7-extraction-" + Guid.NewGuid().ToString("N"));
        var merger = new ExtractionMerger(fixture.Statements);
        var orchestrator = new ExtractionOrchestrator(jobs, new LocalCasBlobStore(root), new DocumentParser(), new Chunker(200, 20),
            new FakeChatClientFactory(), new EndpointCapacityCoordinator(), new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())), new TerminologyService(fixture.Statements),
            new PromptSnapshotService(), merger, fixture.Statements, TimeProvider.System);
        var chunks = new[] { new ChunkSpan(1, "Alice", 0, 5, 1), new ChunkSpan(2, "Bob", 0, 3, 1) };
        var request = new ExtractionRequest(ks.KnowledgeSystemId, "", "test.txt", "fake", "fake", "https://fake.test", null);
        var state = JobState.From(new JobInput(job.Id, ks.KnowledgeSystemId, [1, 2], null!, JobKind.ABoxOnly, null,
            default, ks, request, chunks, []));
        var result = await orchestrator.RunLayerAsync(state, chunks, request.CapacityKey, ks.ABoxGraph, ExtractionPhase.ABox, 0,
            async (chunk, ct) =>
            {
                if (chunk.Idx == 2)
                {
                    Assert.Contains(await fixture.Statements.ListAsync(ks.KnowledgeSystemId, "ABox", ct),
                        item => item.PredicateIri == Vocabulary.RdfsLabel && item.Object == new RdfLiteral("Alice"));
                    await using var progress = await contexts.CreateDbContextAsync(ct);
                    Assert.Equal(1, (await progress.ExtractionJobs.SingleAsync(item => item.Id == job.Id, ct)).ProcessedChunks);
                }
                return new ABoxDelta([new AboxIndividual(chunk.Text, "Person", null, [], [])]);
            }, delta => merger.MergeABox(ks, (ABoxDelta)delta),
            async (id, merged, ct) =>
            {
                if (failAfterMerge) throw new InvalidOperationException("Injected post-merge failure");
                await jobs.RecordABoxMergeAsync(id, merged, ct);
            }, null, default).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(!failAfterMerge, result.Succeeded);
        Assert.Null(fixture.Db.Database.CurrentTransaction);
        var facts = await fixture.Statements.ListAsync(ks.KnowledgeSystemId, "ABox");
        Assert.Contains(other, facts);
        if (failAfterMerge) Assert.Single(facts);
        else
        {
            Assert.Contains(facts, item => item.PredicateIri == Vocabulary.RdfsLabel && item.Object == new RdfLiteral("Alice"));
            Assert.Contains(facts, item => item.PredicateIri == Vocabulary.RdfsLabel && item.Object == new RdfLiteral("Bob"));
        }
    }

    private sealed class ContextFactory(DbContextOptions<ISEStudioDbContext> options) : IDbContextFactory<ISEStudioDbContext>
    {
        public ISEStudioDbContext CreateDbContext() => new(options);
        public Task<ISEStudioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}