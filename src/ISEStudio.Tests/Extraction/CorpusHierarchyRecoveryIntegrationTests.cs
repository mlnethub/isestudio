using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Persistence;
using ISEStudio.Tests.Infrastructure;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Smoke test for the post-TBox corpus / hierarchy recovery passes wired
/// into <see cref="ExtractionOrchestrator"/> (P1-5b slice). The verify
/// critic accepts every class so the corpus recovery's
/// <c>BuildCandidates</c> is empty and short-circuits with zero LLM calls;
/// the hierarchy recovery still issues one <c>HierarchyRecoveryKey</c>
/// prompt per chunk and the test enqueues an empty recovery reply so the
/// pass exits cleanly without admitted classes / edges.
/// </summary>
/// <remarks>
/// Placed in the shared <see cref="ExtractionTestCollection"/> so the
/// background extraction worker can't leak LLM activities into a parallel
/// <see cref="ISEStudio.Tests.Observability.TelemetryTests"/> listener —
/// the worker is alive for several LLM calls and the listener assumes a
/// single activity fires in its capture window.
/// </remarks>
[Collection(ExtractionTestCollection.Name)]
public sealed class CorpusHierarchyRecoveryIntegrationTests : IDisposable
{
    private const string Text = FakeChat.VerifySourceText;

    [Fact]
    public async Task Orchestrator_runs_corpus_and_hierarchy_recovery_between_TBox_and_ABox()
    {
        var root = Path.Combine(Path.GetTempPath(), "isestudio-recovery-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var rdf = new PostgresRdfFixture();
        await rdf.InitializeAsync();
        try
        {
            using var contexts = new SqliteContextFactory();
            var ksId = rdf.KnowledgeSystemId;
            const string graphIri = "http://goodcrew.local/ks/recovery-tests";
            const string baseIri = graphIri + "/onto#";
            var providerId = Guid.NewGuid();
            using (var db = contexts.CreateDbContext())
            {
                db.Providers.Add(new ProviderEntity
                {
                    Id = providerId,
                    Name = "recovery-fixture-llm",
                    BaseUrl = "https://fake.test/v1",
                    ApiKey = "test-key",
                    Model = "fake-model",
                    Kind = "llm",
                    ConcurrencyLimit = 2,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.KnowledgeSystems.Add(new KnowledgeSystemEntity
                {
                    Id = ksId,
                    PublicId = Guid.NewGuid().ToString("N"),
                    Name = "Recovery fixture",
                    GraphIri = graphIri,
                    BaseIri = baseIri,
                    LlmProviderId = providerId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                db.SaveChanges();
            }

            // Seed Person so the delta's property domains resolve against
            // an existing class.
            var seed = SchemaBuilder.BuildMutationStatements(
                baseIri,
                new OntologyMutation(
                    Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                    ObjectProperties: Array.Empty<PropertyMutation>(),
                    DataProperties: Array.Empty<PropertyMutation>(),
                    Axioms: Array.Empty<AxiomMutation>()),
                graphIri);
            rdf.TBox.AddStatements(graphIri, seed);

            var blobs = new LocalCasBlobStore(Path.Combine(root, "blobs"));
            await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(Text)))
            {
                var sha = (await blobs.PutAsync(stream, CancellationToken.None)).Sha256;
                var chat = new FakeChat()
                    .EnqueueValidDelta()       // 1. TBox extractor
                    .EnqueueVerifyAcceptAll()  // 2. critic + 3. denotation
                    .Enqueue("{}");            // 4. hierarchy recovery (no candidates)

                FakeChatClientFactory.Default.Reset();
                FakeChatClientFactory.Default.UseClient(chat);

                var jobs = new ExtractionJobStore(contexts, TimeProvider.System);
                var verifyService = new TBoxVerifyService(Options.Create(new ISEStudioOptions()));
                var options = Options.Create(new ISEStudioOptions());
                var corpusService = new CorpusRecoveryService(options, verifyService);
                var hierarchyService = new HierarchyRecoveryService(options, verifyService);
                ExtractionOrchestrator? orchestrator = null;
                using var services = BuildServices(
                    contexts, rdf, jobs, blobs, verifyService, corpusService, hierarchyService,
                    () => orchestrator ?? throw new InvalidOperationException("Orchestrator is not initialized."));
                orchestrator = new ExtractionOrchestrator(
                    jobs, blobs, new DocumentParser(), new Chunker(size: 400, overlap: 20),
                    FakeChatClientFactory.Default, new EndpointCapacityCoordinator(),
                    new TBoxExtractionService(options), new ABoxExtractionService(options),
                    new TerminologyService(rdf.Statements), new PromptSnapshotService(),
                    new ExtractionMerger(rdf.Statements), rdf.Statements, TimeProvider.System,
                    options, verifyService, corpusService, hierarchyService,
                    services.GetRequiredService<IServiceScopeFactory>());

                var request = new ExtractionRequest(
                    KnowledgeSystemId: ksId,
                    BlobSha: sha,
                    FileName: "recovery-fixture.txt",
                    Provider: "openai",
                    Model: "fake-model",
                    Endpoint: "https://fake.test/v1",
                    ApiKey: null,
                    ConcurrencyLimit: 2);
                var job = await orchestrator.StartTBoxAsync(request, CancellationToken.None);
                var finished = await RunDurableWorkerUntilTerminalAsync(services, jobs, job.Id);

                Assert.Equal("completed", finished.Status);
                // 1 extract + 1 critic + 1 denotation + 1 hierarchy recovery.
                // The corpus recovery short-circuits (BuildCandidates returns
                // empty because the critic accepted every class), so no
                // selector / recovery LLM calls are issued.
                Assert.Equal(4, chat.CallCount);

                // The prompt snapshot now records the four new recovery
                // prompts alongside the three verify prompts.
                var prompts = finished.PromptSnapshot!.RootElement.GetProperty("prompts");
                foreach (var key in new[]
                {
                    CorpusRecoveryService.EvidenceSelectorKey,
                    CorpusRecoveryService.CorpusRecoveryKey,
                    HierarchyRecoveryService.HierarchyCriticKey,
                    HierarchyRecoveryService.HierarchyRecoveryKey,
                })
                {
                    Assert.False(string.IsNullOrWhiteSpace(
                        prompts.GetProperty(key).GetProperty("content").GetString()),
                        $"snapshot should contain {key}");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Stale directory handles on Windows must never fail the run.
            }
            await rdf.DisposeAsync();
        }
    }

    private static ServiceProvider BuildServices(
        SqliteContextFactory contexts,
        PostgresRdfFixture rdf,
        ExtractionJobStore jobs,
        IBlobStore blobs,
        TBoxVerifyService verify,
        CorpusRecoveryService corpus,
        HierarchyRecoveryService hierarchy,
        Func<ExtractionOrchestrator> orchestratorFactory)
    {
        var options = Options.Create(new ISEStudioOptions());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ISEStudioDbContext>>(contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ISEStudioDbContext>>()
                .CreateDbContext());
        services.AddSingleton<IRdfStatementRepository>(rdf.Statements);
        services.AddSingleton(jobs);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(verify);
        services.AddSingleton(corpus);
        services.AddSingleton(hierarchy);
        services.AddScoped<EmbeddingGeneratorFactory>();
        services.AddScoped<DuplicateJudge>();
        services.AddScoped<ConflictService>();
        services.AddScoped<IConflictAgent, ConflictAgent>();
        services.AddScoped<IStructureAgent, StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<IKnowledgeStatsService, KnowledgeStatsService>();
        services.AddScoped<TerminologyAgent>();
        services.AddSingleton(blobs);
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddSingleton(new Chunker(size: 400, overlap: 20));
        services.AddSingleton(new EndpointCapacityCoordinator());
        services.AddSingleton(new TBoxExtractionService(options));
        services.AddSingleton(new ABoxExtractionService(options));
        services.AddSingleton<ITerminologySync>(new TerminologyService(rdf.Statements));
        services.AddSingleton(new PromptSnapshotService());
        services.AddSingleton<IExtractionMerger>(new ExtractionMerger(rdf.Statements));
        services.AddDovetailPipelines();
        services.AddScoped<IExtractionJobHandler, TBoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, ABoxExtractionJobHandler>();
        services.AddScoped<IExtractionJobHandler, CombinedExtractionJobHandler>();
        services.AddScoped<ExtractionJobDispatcher>();
        services.AddSingleton<ExtractionOrchestrator>(_ => orchestratorFactory());
        return services.BuildServiceProvider();
    }

    private static async Task<ExtractionJobEntity> RunDurableWorkerUntilTerminalAsync(
        ServiceProvider services,
        ExtractionJobStore jobs,
        Guid jobId)
    {
        var worker = new DurableExtractionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            jobs,
            TimeProvider.System,
            NullLogger<DurableExtractionWorker>.Instance,
            Options.Create(new DurableExtractionWorkerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                SupportedKinds = new[] { ExtractionWire.KindTBox, ExtractionWire.KindABox, ExtractionWire.KindBoth },
            }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var workerTask = worker.StartAsync(cancellation.Token);
        try
        {
            return await jobs.WaitAsync(jobId, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await worker.StopAsync(CancellationToken.None);
            await workerTask;
        }
    }

    /// <inheritdoc />
    public void Dispose() => FakeChatClientFactory.Default.Reset();
}