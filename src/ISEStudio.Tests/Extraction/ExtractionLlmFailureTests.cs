using System.Text;
using Microsoft.Extensions.AI;
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
/// Verifies Stage 3 finding I3: when the chat client raises a non-transient
/// exception (e.g. auth failure, configuration error), the orchestrator
/// must catch it in its per-phase <c>try</c>, revert the RDF capture, and
/// mark the job failed. Silently returning <c>TBoxDelta.Empty</c> would
/// let a provider outage masquerade as a successful empty extraction.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionLlmFailureTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/llm-failure-tests";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly StoreWrapper _store;
    private readonly ExtractionJobStore _jobs;
    private readonly ExtractionOrchestrator _orchestrator;
    private readonly ThrowingChat _chat;
    private readonly string _sha;

    public ExtractionLlmFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-llm-failure-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();

        var blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        _sha = PutDocument(blobs);

        _jobs = new ExtractionJobStore(_contexts, TimeProvider.System);
        _chat = new ThrowingChat(new InvalidOperationException("simulated provider auth failure"));

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(_chat);

        _orchestrator = new ExtractionOrchestrator(
            _jobs,
            blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(_store),
            new PromptSnapshotService(),
            new ExtractionMerger(_store),
            _store,
            TimeProvider.System);
    }

    /// <summary>
    /// Regression for Stage 3 finding I3: a non-transient chat failure
    /// (here an <see cref="InvalidOperationException"/>) must propagate out
    /// of the extraction service, get caught by the orchestrator's per-phase
    /// <c>try</c>, and the job must be marked failed with the error message
    /// preserved — not silently reported as completed with zero axioms.
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Non_transient_chat_failure_marks_job_failed_and_reverts_capture()
    {
        var before = ClassCount();

        var job = await _orchestrator.StartTBoxAsync(
            NewRequest(),
            CancellationToken.None);
        var finished = await _jobs.WaitAsync(job.Id);

        Assert.Equal(
            "failed",
            finished.Status);
        Assert.Contains("simulated provider auth failure", finished.Error ?? string.Empty);
        Assert.NotNull(finished.FinishedAt);

        // The seeded TBox is still exactly as it was: no orphan triples
        // from a half-merged run.
        Assert.Equal(before, ClassCount());
    }

    /// <summary>
    /// Companion regression: transient HTTP failures (<see cref="HttpRequestException"/>)
    /// are still tolerated so a single bad chunk does not abort the whole
    /// job. The job completes with zero axioms (the chat returned no usable
    /// delta on every chunk), but the status is "completed", not "failed".
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task Transient_chat_failure_is_tolerated_and_job_completes()
    {
        // Replace the fixture's chat with a transient-failure chat for
        // this test only.
        _chat.Exception = new HttpRequestException("simulated provider outage");

        var job = await _orchestrator.StartTBoxAsync(
            NewRequest(),
            CancellationToken.None);
        var finished = await _jobs.WaitAsync(job.Id);

        Assert.Equal("completed", finished.Status);
        Assert.Equal(0, finished.AxiomsAdded);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        _store.Dispose();
        _contexts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- helpers ----

    private ExtractionRequest NewRequest() => new(
        KnowledgeSystemId: _ksId,
        BlobSha: _sha,
        FileName: "llm-failure-fixture.txt",
        Provider: "openai",
        Model: "fake-model",
        Endpoint: "https://fake.test/v1",
        ApiKey: null,
        ConcurrencyLimit: 2);

    private int ClassCount() =>
        _store.Match(
            predicateIri: Vocabulary.RdfType.Value,
            objectIri: Vocabulary.OwlClass.Value,
            graphIri: GraphIri).Count;

    private void SeedTBox()
    {
        var quads = SchemaBuilder.BuildMutation(
            BaseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Person", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: new[] { new PropertyMutation("age", "data", Domain: "Person", Range: "integer") },
                Axioms: Array.Empty<AxiomMutation>()),
            GraphIri);
        _store.AddQuads(new Oxigraph.NamedNode(GraphIri), quads);
    }

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "LLM failure fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
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

    /// <summary>
    /// Chat client that raises a configurable exception on every call.
    /// The exception can be swapped mid-test (e.g. from
    /// <see cref="InvalidOperationException"/> to
    /// <see cref="HttpRequestException"/>) without rebuilding the fixture.
    /// </summary>
    private sealed class ThrowingChat : IChatClient
    {
        private readonly object _lock = new();
        private Exception _exception;

        public ThrowingChat(Exception exception) => _exception = exception;

        public Exception Exception
        {
            get { lock (_lock) return _exception; }
            set { lock (_lock) _exception = value; }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var ex = Exception;
            return Task.FromException<ChatResponse>(ex);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}