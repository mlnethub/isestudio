using System.Text;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Persistence;

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
        try
        {
            using var store = new StoreWrapper(Path.Combine(root, "store"));
            using var contexts = new SqliteContextFactory();
            var ksId = Guid.NewGuid();
            const string graphIri = "http://goodcrew.local/ks/recovery-tests";
            const string baseIri = graphIri + "/onto#";
            using (var db = contexts.CreateDbContext())
            {
                db.KnowledgeSystems.Add(new KnowledgeSystemEntity
                {
                    Id = ksId,
                    PublicId = Guid.NewGuid().ToString("N"),
                    Name = "Recovery fixture",
                    GraphIri = graphIri,
                    BaseIri = baseIri,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                db.SaveChanges();
            }

            // Seed Person so the delta's property domains resolve against
            // an existing class.
            store.AddQuads(new Oxigraph.NamedNode(graphIri), SchemaBuilder.BuildMutation(
                baseIri,
                new OntologyMutation(
                    Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                    ObjectProperties: Array.Empty<PropertyMutation>(),
                    DataProperties: Array.Empty<PropertyMutation>(),
                    Axioms: Array.Empty<AxiomMutation>()),
                graphIri));

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
                var orchestrator = new ExtractionOrchestrator(
                    jobs,
                    blobs,
                    new DocumentParser(),
                    new Chunker(size: 400, overlap: 20),
                    FakeChatClientFactory.Default,
                    new EndpointCapacityCoordinator(),
                    new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
                    new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
                    new TerminologyService(store),
                    new PromptSnapshotService(),
                    new FakeMerger(new ExtractionMerger(store)),
                    store,
                    TimeProvider.System,
                    verify: verifyService,
                    corpus: new CorpusRecoveryService(
                        Options.Create(new ISEStudioOptions()), verifyService),
                    hierarchy: new HierarchyRecoveryService(
                        Options.Create(new ISEStudioOptions()), verifyService));

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
                var finished = await jobs.WaitAsync(job.Id);

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
                // Stale Oxigraph handles on Windows must never fail the run.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => FakeChatClientFactory.Default.Reset();
}