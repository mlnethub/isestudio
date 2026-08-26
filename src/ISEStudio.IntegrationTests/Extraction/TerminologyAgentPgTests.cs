using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Extraction;

/// <summary>
/// PostgreSQL-backed integration test for
/// <see cref="TerminologyAgent.SuggestAsync"/>. The agent's SQLite
/// path is covered by
/// <c>src/ISEStudio.Tests/Extraction/TerminologyAgentOrchestrationTests.cs</c>;
/// this fixture proves the same flow works against a real PG backend,
/// which is what production runs on.
///
/// <para>
/// What this catches that the SQLite path cannot:
/// <list type="bullet">
///   <item>EF Core's Npgsql provider formats SQL differently from
///   SQLite (case-sensitive string contains, bytea handling, JSON
///   column types). The grounding check + payload JSON serialization
///   must survive the provider swap.</item>
///   <item>The integration suite's "only-on-PG" path: extracting
///   terminology proposals end-to-end on the same backend the
///   cutover migration targets.</item>
/// </list>
/// </para>
///
/// <remarks>
/// Tests skip silently when docker is unavailable (Windows container
/// without a docker daemon, sandboxed CI runner). The skip pattern
/// mirrors <see cref="ISEStudio.IntegrationTests.Migration.IriSqlVerifierTests"/>'s
/// soft-return docker gate so the integration test baseline never
/// regresses to "DockerException everywhere".
/// </remarks>
/// </summary>
[Trait("Category", "Extraction")]
public sealed class TerminologyAgentPgTests : IAsyncLifetime
{
    private const string GraphIri = "http://goodcrew.local/ks/term-agent-pg";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("isestudio_term_pg")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        try
        {
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            _dockerAvailable = true;
        }
        catch (Exception ex) when (
            ex is Docker.DotNet.DockerApiException
            || ex is System.Net.Http.HttpRequestException
            || ex is TimeoutException
            || ex is InvalidOperationException)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_dockerAvailable)
        {
            await _container.DisposeAsync();
        }
    }

    private bool DockerRequired()
    {
        if (_dockerAvailable) return false;
        return true;
    }

    /// <summary>
    /// Build a fresh DI scope around a real PG-backed DbContext. The
    /// scoped lifetime is important: <see cref="TerminologyAgent"/>
    /// resolves the DbContext per request, mirroring the production
    /// pipeline that <see cref="ExtractionOrchestrator"/> uses.
    /// <para>
    /// DbContext is wired via <see cref="IDbContextFactory{TContext}"/>
    /// (singleton) — same pattern as production in
    /// <c>src/ISEStudio/Program.cs:280-310</c>. Each
    /// <c>sp.GetRequiredService&lt;ISEStudioDbContext&gt;()</c>
    /// resolves a fresh context through the factory, so test code can
    /// safely dispose one and resolve another in the same scope
    /// without tripping the "captive dependency" / "disposed context"
    /// traps.
    /// </para>
    /// </summary>
    private ServiceProvider BuildServices(IChatClient chat)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_ =>
        {
            var options = new DbContextOptionsBuilder<ISEStudioDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new PgDbContextFactory(options);
        });
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        // Inline the chat-client factory so this test does not depend
        // on the ISEStudio.Tests project's FakeChatClientFactory. The
        // factory simply returns the chat the test installed; the
        // production factory's per-provider wiring (API key, URL,
        // model) is irrelevant to a unit-style integration test that
        // already controls the canned reply.
        var inline = new InlineChatClientFactory(chat);
        services.AddSingleton<IChatClientFactory>(inline);
        services.AddSingleton(Options.Create(new ISEStudioOptions
        {
            TerminologySuggestionMaxChunks = 50,
            TerminologySuggestDuringExtraction = true,
        }));
        services.AddScoped<TerminologyAgent>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> for the PG
    /// integration test. Mirrors EF Core's built-in factory wiring
    /// but is defined inline so the test does not depend on
    /// <c>SqliteContextFactory</c> (which is part of the unit-test
    /// project and uses a different provider).
    /// </summary>
    private sealed class PgDbContextFactory : IDbContextFactory<ISEStudioDbContext>
    {
        private readonly DbContextOptions<ISEStudioDbContext> _options;
        public PgDbContextFactory(DbContextOptions<ISEStudioDbContext> options) => _options = options;
        public ISEStudioDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Seed a minimal fixture: provider + knowledge system + document
    /// + chunk. The chunk text is "A centrifugal pump uses an
    /// impeller to move fluid outward by rotational energy." so the
    /// FakeChat reply proposing "Impeller" passes the _source_contains
    /// grounding check (P3-8) on PG just as it does on SQLite.
    /// <para>
    /// DbContext is NOT disposed at end of helper — the DI scope owns
    /// the context's lifetime, and the agent + the verification
    /// follow-up read both need to share it (or get fresh contexts
    /// from the factory within the same scope).
    /// </para>
    /// </summary>
    private async Task<(Guid ksId, long chunkLegacyId)> SeedFixtureAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<ISEStudioDbContext>();
        await db.Database.MigrateAsync();

        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = NextLegacyId("provider"),
            Name = "term-agent-pg-llm",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);

        var ksId = Guid.NewGuid();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = ksId,
            LegacyId = NextLegacyId("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term agent PG fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        const string text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = NextLegacyId("document"),
            KnowledgeSystemId = ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "pump.txt",
            Folder = "/",
            ParseStatus = "parsed",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);

        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = NextLegacyId("chunk"),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();
        return (ksId, chunk.LegacyId);
    }

    /// <summary>
    /// Canned LLM reply the agent's chat client must parse: one
    /// <c>create</c> proposal whose <c>preferred_label</c> appears
    /// verbatim in the seeded chunk text.
    /// </summary>
    private static string ProposeReply(long chunkLegacyId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "Impeller",
            "language": "en",
            "alternate_labels": [],
            "description": "Rotating component of a centrifugal pump",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "explicit component in source",
            "source_chunk_ids": [{{chunkLegacyId}}]
          }]
        }
        """;

    private static string HallucinatedReply(long chunkLegacyId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "Compressor",
            "language": "en",
            "alternate_labels": [],
            "description": "Device that pressurises gas",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "hallucinated term not in source",
            "source_chunk_ids": [{{chunkLegacyId}}]
          }]
        }
        """;

    [Fact]
    public async Task SuggestAsync_persists_pending_proposal_on_postgres()
    {
        // End-to-end PG smoke: spin up Testcontainers PG, seed a
        // minimal fixture, resolve a real TerminologyAgent from DI,
        // run SuggestAsync, and assert one pending row landed in the
        // term_proposals table on the PG backend.
        if (DockerRequired()) return;

        var chat = new CannedReplyChat(ProposeReply(0));
        var services = BuildServices(chat);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var (ksId, chunkLegacyId) = await SeedFixtureAsync(sp);
            chat.SetNextReply(ProposeReply(chunkLegacyId));

            var ks = await LoadKsAsync(sp, ksId);
            Assert.NotNull(ks);

            var agent = sp.GetRequiredService<TerminologyAgent>();
            var rows = await agent.SuggestAsync(
                ks: ks!,
                schemeIri: GraphIri + "/scheme",
                chunkIds: new[] { chunkLegacyId },
                model: null,
                ct: CancellationToken.None);

            // The agent must produce one row (the grounding check passes
            // because "Impeller" appears in the seeded chunk text).
            var row = Assert.Single(rows);
            Assert.Equal("create", row.Action);
            Assert.Equal("Impeller", row.Term);
            Assert.Equal("terminology-agent", row.ProposedBy);
            Assert.Equal("pending", row.Status);

            // PG persistence assertion: the row must be readable through
            // a fresh context, with LegacyId allocated by the PG
            // advisory-lock allocator (not the SQLite MAX-of-empty
            // shortcut the SQLite fixture exercises). We use the factory
            // directly so the verification context is independent of
            // the agent's scoped context — disposing one does not
            // affect the other.
            var factory = sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
            await using var verifyDb = factory.CreateDbContext();
            var persisted = await verifyDb.TermProposals
                .Where(p => p.KnowledgeSystemId == ksId)
                .ToListAsync();
            var persistedRow = Assert.Single(persisted);
            Assert.Equal(row.Signature, persistedRow.Signature);
            Assert.True(persistedRow.LegacyId > 0,
                $"expected LegacyId > 0 from PG allocator, got {persistedRow.LegacyId}");
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task SuggestAsync_returns_zero_rows_when_chunks_do_not_contain_term()
    {
        // PG parity for the P3-8 grounding check: the agent must drop
        // proposals whose preferred_label is NOT a substring of any
        // cited chunk text, regardless of the backend. The chunk
        // fixture's text is "centrifugal pump … impeller"; this reply
        // proposes "Compressor" which has no anchor — the agent must
        // return empty, and no row must be persisted on PG.
        if (DockerRequired()) return;

        var chat = new CannedReplyChat(HallucinatedReply(0));
        var services = BuildServices(chat);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var (ksId, chunkLegacyId) = await SeedFixtureAsync(sp);
            chat.SetNextReply(HallucinatedReply(chunkLegacyId));

            var ks = await LoadKsAsync(sp, ksId);
            Assert.NotNull(ks);

            var agent = sp.GetRequiredService<TerminologyAgent>();
            var rows = await agent.SuggestAsync(
                ks: ks!,
                schemeIri: GraphIri + "/scheme",
                chunkIds: new[] { chunkLegacyId },
                model: null,
                ct: CancellationToken.None);

            Assert.Empty(rows);

            // Read through a fresh factory-created context to avoid
            // sharing the agent's scoped context (whose entries are
            // already tracked from SaveChanges).
            var factory = sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>();
            await using var verifyDb = factory.CreateDbContext();
            Assert.Empty(await verifyDb.TermProposals
                .Where(p => p.KnowledgeSystemId == ksId)
                .ToListAsync());
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    private static async Task<KnowledgeSystemEntity?> LoadKsAsync(IServiceProvider sp, Guid ksId)
    {
        // DbContext lifetime is owned by the scope, NOT by this helper.
        // Disposing the context here would dispose the very instance the
        // agent (resolved next) needs for its query.
        var db = sp.GetRequiredService<ISEStudioDbContext>();
        return await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.Id == ksId);
    }

    // ------------------------------------------------------------------
    // Inline test doubles (kept inside the integration test file so the
    // ISEStudio.IntegrationTests project does not need to reference
    // ISEStudio.Tests for FakeChatClientFactory + FakeChat).
    // ------------------------------------------------------------------

    /// <summary>
    /// Per-prefix monotonic legacy-id allocator. Mirrors the
    /// contract of <c>ISEStudio.Tests.Persistence.TestLegacyIds.Next</c>
    /// without the project reference — each call returns a unique id
    /// per prefix so multiple entity rows in the same fixture don't
    /// collide on the unique index.
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> _nextIds = new();
    private static long NextLegacyId(string prefix) =>
        _nextIds.AddOrUpdate(prefix, 1000L, (_, current) => current + 1);

    /// <summary>
    /// <see cref="IChatClientFactory"/> that returns the chat the test
    /// installed. Production <c>IChatClientFactory</c> wires per-provider
    /// API key / URL / model; that wiring is irrelevant to a PG
    /// integration test where the chat already returns canned replies.
    /// </summary>
    private sealed class InlineChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _chat;
        public InlineChatClientFactory(IChatClient chat) => _chat = chat;
        public IChatClient Create(LlmProviderConfig config) => _chat;
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> stub that returns a single
    /// canned reply (or "null / throw" via <see cref="SetNextReply"/>
    /// before the agent fires). No streaming support — the agent only
    /// uses the unary <see cref="GetResponseAsync"/> overload.
    /// </summary>
    private sealed class CannedReplyChat : IChatClient
    {
        private string _reply;
        public CannedReplyChat(string reply) => _reply = reply;
        public void SetNextReply(string reply) => _reply = reply;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}