using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;

namespace ISEStudio.IntegrationTests.Extraction;

/// <summary>
/// End-to-end orchestrator smoke tests that wire the full collaborator graph
/// (real <see cref="StoreWrapper"/>, real <see cref="LocalCasBlobStore"/>,
/// real <see cref="DocumentParser"/> + <see cref="Chunker"/>, real
/// <see cref="ExtractionMerger"/>, <see cref="TerminologyService"/>) but
/// stub out the LLM via <see cref="ITChatClient"/> so the run completes
/// without contacting any external service.
///
/// <para>The integration test harness uses an in-memory SQLite database
/// rather than the Testcontainers-managed Postgres so the workflow tests
/// stay self-contained and run as fast as the unit suite. The Postgres
/// coverage (schema, FKs, jsonb/bytea types) lives in
/// <c>Persistence.PostgresSchemaTests</c>.</para>
/// </summary>
public sealed class ExtractionWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly StoreWrapper _store;
    private readonly IDbContextFactory<ISEStudioDbContext> _contexts;
    private readonly LocalCasBlobStore _blobs;
    private readonly ITChatClient _chat = new();

    public ExtractionWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-extraction-it-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));

        // Per-test in-memory SQLite (unique shared-cache name) so the
        // schema is private to this fixture and concurrent context creation
        // does not race inside Microsoft.Data.Sqlite.
        var cacheName = $"isestudio-it-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:memdb-{cacheName}?mode=memory&cache=shared";
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            connection.Open();
            using var bootstrap = new ISEStudioDbContext(new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseSqlite(connection)
                .Options);
            bootstrap.Database.EnsureCreated();
        }
        _contexts = new SharedCacheContextFactory(connectionString);
        _blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
    }

    /// <summary>
    /// Sanity check: the upload → extract → poll pipeline produces a row that
    /// transitions from <c>pending</c> → <c>running</c> → <c>completed</c>
    /// and the RDF store ends up with the axioms the canned chat reply
    /// described. Failure here is the regression smoke for the whole
    /// orchestrator surface.
    /// </summary>
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task End_to_end_upload_extract_poll()
    {
        // Seed a knowledge system so the orchestrator can resolve the
        // graph IRI from the row (production contract).
        var ksId = Guid.NewGuid();
        var graphIri = $"http://goodcrew.local/it/{ksId:N}";
        var baseIri = $"{graphIri}/onto#";
        using (var db = _contexts.CreateDbContext())
        {
            db.KnowledgeSystems.Add(new Infrastructure.Persistence.Entities.KnowledgeSystemEntity
            {
                Id = ksId,
                PublicId = ksId.ToString("N"),
                Name = "IT extraction",
                GraphIri = graphIri,
                BaseIri = baseIri,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Upload a small document and queue canned TBox deltas.
        var text = "A Cat is a domesticated feline. A Cat has a name. Felix is a Cat named Felix.";
        string sha;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
        {
            sha = (await _blobs.PutAsync(stream, CancellationToken.None)).Sha256;
        }
        _chat.EnqueueValidDeltas(4);

        var jobs = new ExtractionJobStore(_contexts, TimeProvider.System);
        var orchestrator = new ExtractionOrchestrator(
            jobs,
            _blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            new SingleClientFactory(_chat),
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(_store),
            new PromptSnapshotService(),
            new ExtractionMerger(_store),
            _store,
            TimeProvider.System);

        var job = await orchestrator.StartTBoxAsync(
            new ExtractionRequest(
                KnowledgeSystemId: ksId,
                BlobSha: sha,
                FileName: "fixture.txt",
                Provider: "fake",
                Model: "fake-model",
                Endpoint: "https://fake.test/v1",
                ApiKey: null),
            CancellationToken.None);

        Assert.Equal("pending", job.Status);

        var finished = await jobs.WaitAsync(job.Id);
        Assert.Equal("completed", finished.Status);
        Assert.True(finished.TotalChunks > 0);
        Assert.Equal(finished.TotalChunks, finished.ProcessedChunks);

        // The TBox graph for this KS must now contain at least one
        // owl:Class quad (the chat reply always mints a fresh class).
        var classCount = _store.Match(
            predicateIri: Vocabulary.RdfType.Value,
            objectIri: Vocabulary.OwlClass.Value,
            graphIri: graphIri).Count;
        Assert.True(classCount > 0, "Extracted classes should land in the KS TBox graph.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Always returns the same <see cref="ITChatClient"/> instance.</summary>
    private sealed class SingleClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(LlmProviderConfig config) => _client;
    }

    /// <summary>
    /// Connection-per-context factory pointing at a shared in-memory SQLite
    /// cache. Mirrors the production-grade unit-test factory in
    /// <c>ISEStudio.Tests/Extraction/SqliteContextFactory.cs</c>; duplicated
    /// here so the IntegrationTests project does not need a project
    /// reference onto the unit-test assembly.
    /// </summary>
    private sealed class SharedCacheContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly string _connectionString;

        public SharedCacheContextFactory(string connectionString) => _connectionString = connectionString;

        public ISEStudioDbContext CreateDbContext()
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            connection.Open();
            return new ISEStudioDbContext(new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseSqlite(connection)
                .Options);
        }
    }

    /// <summary>
    /// Minimal canned-reply chat client for the workflow smoke test. Mirrors
    /// <c>ISEStudio.Tests/Extraction/FakeChat.cs</c>; duplicated here so the
    /// IntegrationTests project does not need a project reference onto the
    /// unit-test assembly.
    /// </summary>
    private sealed class ITChatClient : IChatClient
    {
        private const string ValidTBoxDelta = """
            {
              "classes": [
                {"label": "Animal", "comment": "A living creature"},
                {"label": "Cat", "comment": "A domesticated feline"}
              ],
              "object_properties": [
                {"label": "owns", "domain": "Person", "range": "Animal"}
              ],
              "data_properties": [
                {"label": "name", "domain": "Animal", "range": "string"}
              ],
              "subclass_of": [{"sub": "Cat", "super": "Animal"}],
              "disjoint_with": [],
              "equivalent_class": []
            }
            """;

        private readonly Queue<string> _replies = new();
        private readonly object _gate = new();
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public ITChatClient EnqueueValidDelta() { lock (_gate) _replies.Enqueue(ValidTBoxDelta); return this; }

        public ITChatClient EnqueueValidDeltas(int count)
        {
            for (var i = 0; i < count; i++) EnqueueValidDelta();
            return this;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            string reply;
            lock (_gate) reply = _replies.Count > 0 ? _replies.Dequeue() : "{}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
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