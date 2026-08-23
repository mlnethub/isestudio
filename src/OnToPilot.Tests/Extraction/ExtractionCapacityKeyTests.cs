using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Storage;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// Verifies Constraint 4: the chat capacity bucket is keyed by the provider
/// endpoint, not the knowledge-system graph IRI. Two jobs pointed at the
/// same provider must share a permit budget; two jobs pointed at different
/// providers must flow through independent buckets.
///
/// <para>Each test wires the orchestrator with a single
/// <see cref="EndpointCapacityCoordinator"/> and runs two jobs against two
/// distinct knowledge systems (so the per-graph <c>CaptureAsync</c> lock
/// does not interfere) — only the chat capacity bucket is allowed to vary.
/// The chat client is parked from the first call so the chat call counter
/// reflects how many jobs actually entered the chat: a job that acquired a
/// permit always increments the counter, a job blocked at the semaphore
/// never does.</para>
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionCapacityKeyTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly StoreWrapper _store;
    private readonly LocalCasBlobStore _blobs;
    private readonly ExtractionJobStore _jobs;
    private readonly ExtractionOrchestrator _orchestrator;
    private readonly FakeChat _chat = new();
    private readonly FakeMerger _merger;
    private readonly string _sha;

    public ExtractionCapacityKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ontopilot-capacity-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));
        _blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        _contexts = new SqliteContextFactory();
        _sha = PutDocument(_blobs);

        _jobs = new ExtractionJobStore(_contexts, TimeProvider.System);
        _merger = new FakeMerger(new ExtractionMerger(_store));

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(_chat);

        // ConcurrencyLimit = 1 so each bucket holds exactly one permit:
        // any two jobs on the same endpoint must serialise on that permit,
        // while two jobs on different endpoints each draw their own permit
        // from independent buckets.
        _orchestrator = new ExtractionOrchestrator(
            _jobs,
            _blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new OnToPilotOptions())),
            new ABoxExtractionService(Options.Create(new OnToPilotOptions())),
            new TerminologyService(_store),
            new PromptSnapshotService(),
            _merger,
            _store,
            TimeProvider.System);
    }

    /// <summary>
    /// Regression for Stage 3 finding C1: two jobs on the same provider
    /// endpoint share one permit bucket. With <c>ConcurrencyLimit = 1</c>
    /// and the chat client parked from the very first call, only one job
    /// ever reaches the chat — the other is blocked at the semaphore
    /// because the bucket has just one permit.
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Same_endpoint_jobs_share_the_chat_permit_bucket()
    {
        // Two knowledge systems → independent graph IRIs → no
        // cross-contention on the per-graph CaptureAsync lock.
        var ksA = SeedKnowledgeSystem("ks-a");
        var ksB = SeedKnowledgeSystem("ks-b");
        SeedTBox(ksA);
        SeedTBox(ksB);

        // Park every chat call so we can observe how many jobs ever
        // reached the chat (one permit = at most one job enters).
        _chat.BlockAfter(0);

        var requestA = NewRequest(ksA, endpoint: "https://provider-a.test/v1");
        var requestB = NewRequest(ksB, endpoint: "https://provider-a.test/v1");

        var jobA = await _orchestrator.StartTBoxAsync(requestA, CancellationToken.None);
        AllocateLegacyId(jobA.Id);
        var jobB = await _orchestrator.StartTBoxAsync(requestB, CancellationToken.None);
        AllocateLegacyId(jobB.Id);

        // Give both background tasks a chance to enter the chat. After
        // ~500ms the scheduler has run both jobs to the semaphore wait;
        // whichever job got the permit is parked in chat, the other is
        // blocked at the semaphore and never entered chat.
        await Task.Delay(500);
        var observed = _chat.CallCount;
        Assert.True(
            observed == 1,
            $"Expected exactly 1 chat call (one permit); observed {observed}.");

        // Cleanup: unblock the parked chat so the background tasks can
        // wind down without leaking waiters into the test runner.
        _chat.Release();
    }

    /// <summary>
    /// Regression for Stage 3 finding C1: two jobs on different provider
    /// endpoints draw from independent buckets. Both can hold a permit
    /// simultaneously, so both reach the chat in parallel even when every
    /// call is parked.
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Different_endpoint_jobs_do_not_block_each_other()
    {
        var ksA = SeedKnowledgeSystem("ks-a");
        var ksB = SeedKnowledgeSystem("ks-b");
        SeedTBox(ksA);
        SeedTBox(ksB);

        // Park every chat call so we can observe how many jobs ever
        // reached the chat (one permit per bucket = both jobs enter).
        _chat.BlockAfter(0);

        var requestA = NewRequest(ksA, endpoint: "https://provider-a.test/v1");
        var requestB = NewRequest(ksB, endpoint: "https://provider-b.test/v1");

        var jobA = await _orchestrator.StartTBoxAsync(requestA, CancellationToken.None);
        AllocateLegacyId(jobA.Id);
        var jobB = await _orchestrator.StartTBoxAsync(requestB, CancellationToken.None);
        AllocateLegacyId(jobB.Id);

        // Give both background tasks a chance to enter the chat. Each
        // holds its own permit, so both should have entered the chat
        // by the time the wait elapses.
        await Task.Delay(500);
        var observed = _chat.CallCount;
        Assert.True(
            observed >= 2,
            $"Expected both jobs to enter the chat (independent permits); observed {observed}.");

        _chat.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        _chat.Release();
        _store.Dispose();
        _contexts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----

    private ExtractionRequest NewRequest(Guid knowledgeSystemId, string endpoint) => new(
        KnowledgeSystemId: knowledgeSystemId,
        BlobSha: _sha,
        FileName: "capacity-fixture.txt",
        Provider: "openai",
        Model: "fake-model",
        Endpoint: endpoint,
        ApiKey: null,
        ConcurrencyLimit: 1);

    /// <summary>
    /// The job store's <c>CreateAsync</c> defaults <c>LegacyId = 0</c>; two
    /// jobs in one fixture would trip the SQLite unique index. Allocate a
    /// distinct legacy id per row so both inserts succeed.
    /// </summary>
    private void AllocateLegacyId(Guid jobId)
    {
        using var db = _contexts.CreateDbContext();
        var job = db.ExtractionJobs.First(j => j.Id == jobId);
        job.LegacyId = TestLegacyIds.Next("extractionjob");
        db.SaveChanges();
    }

    private Guid SeedKnowledgeSystem(string tag)
    {
        var id = Guid.NewGuid();
        var graphIri = $"http://goodcrew.local/{tag}/{id:N}";
        var baseIri = $"{graphIri}/onto#";
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = id,
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = $"Capacity fixture {tag}",
            GraphIri = graphIri,
            BaseIri = baseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private void SeedTBox(Guid knowledgeSystemId)
    {
        using var db = _contexts.CreateDbContext();
        var ks = db.KnowledgeSystems.First(k => k.Id == knowledgeSystemId);
        var graphIri = ks.GraphIri;
        var baseIri = ks.BaseIri;
        var quads = SchemaBuilder.BuildMutation(
            baseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: new[] { new PropertyMutation("age", "data", Domain: "Person", Range: "integer") },
                Axioms: Array.Empty<AxiomMutation>()),
            graphIri);
        _store.AddQuads(new Oxigraph.NamedNode(graphIri), quads);
    }

    private static string PutDocument(IBlobStore blobs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < 4; i++)
        {
            text.Append(
                $"Section {i}. A Person is a human being with an age. " +
                $"An Employee is a Person who works for an organisation. " +
                $"Alice is a Person aged forty two in section {i}.\n\n");
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }
}