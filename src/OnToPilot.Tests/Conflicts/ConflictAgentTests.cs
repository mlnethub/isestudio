using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnToPilot.Conflicts;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Conflicts;

/// <summary>
/// Unit tests for the agentic conflict triage (.NET port of
/// <c>backend/app/ontology/conflict_agent.py</c>). The agent attaches a
/// <c>payload.recommendation</c> to open <c>duplicate</c> /
/// <c>predicate_specialization</c> conflicts after a short ReAct tool loop
/// and never auto-applies (Python <c>AUTO_APPLY_TYPES</c> is empty).
/// Structural conflicts are untouched; every LLM hiccup leaves the conflict
/// for a human instead of failing.
///
/// <para>Each test owns a <see cref="FakeChat"/> instance (fresh per test
/// method via xUnit's per-test class construction), so the tests run in
/// parallel without shared chat state. The DB is a shared-cache SQLite
/// database through <see cref="SqliteContextFactory"/>; the Oxigraph store
/// is a per-instance RocksDB temp directory.</para>
/// </summary>
public sealed class ConflictAgentTests : IDisposable
{
    private readonly SqliteContextFactory _dbFactory = new();
    private readonly string _storePath;
    private readonly StoreWrapper _store;
    private readonly FakeChat _chat = new();
    private readonly FakeChatClientFactory _chatFactory = new();

    public ConflictAgentTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "ontopilot-conflict-agent-" + Guid.NewGuid().ToString("N"));
        _store = new StoreWrapper(_storePath);
        _chatFactory.UseClient(_chat);
    }

    public void Dispose()
    {
        _chatFactory.Reset();
        _store.Dispose();
        _dbFactory.Dispose();
        if (Directory.Exists(_storePath))
        {
            Directory.Delete(_storePath, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Triage_attaches_recommendation_for_predicate_specialization()
    {
        var ksId = await SeedWorkspaceAsync("attach");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge", "subprop"));

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.92,"reason":"same relation, range noun baked in"}""");

        var log = await RunTriageAsync(ksId, new OnToPilotOptions { SystemLanguage = "en" });

        Assert.Single(log);
        Assert.Contains("recommend", log[0]);

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        var payload = row.Payload!.RootElement;
        // Existing payload keys survive the merge (Python {**c.payload, ...}).
        Assert.True(payload.TryGetProperty("resolutions", out _));
        var rec = payload.GetProperty("recommendation");
        Assert.Equal("merge", rec.GetProperty("resolution_id").GetString());
        Assert.Equal(0.92, rec.GetProperty("confidence").GetDouble());
        Assert.Equal("same relation, range noun baked in", rec.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Triage_parses_string_confidence()
    {
        var ksId = await SeedWorkspaceAsync("conf-string");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        // Python float(str(...)) accepts string confidences; the port must too.
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":"0.75","reason":"ok"}""");

        await RunTriageAsync(ksId, new OnToPilotOptions());

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal(0.75, row.Payload!.RootElement
            .GetProperty("recommendation").GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Triage_skip_and_unknown_resolution_leave_conflicts_untouched()
    {
        var ksId = await SeedWorkspaceAsync("skip");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));
        await SeedConflictAsync(ksId, "duplicate", PayloadWithResolutions("keep-general"));

        _chat.Enqueue("""{"action":"finish","resolution":"skip","confidence":0.1,"reason":"unsure"}""");
        _chat.Enqueue("""{"action":"finish","resolution":"bogus-id","confidence":0.9,"reason":"hallucinated"}""");

        var log = await RunTriageAsync(ksId, new OnToPilotOptions());

        Assert.Empty(log);
        Assert.Equal(2, _chat.CallCount);

        await using var verify = _dbFactory.CreateDbContext();
        var rows = await verify.Conflicts.Where(c => c.KnowledgeSystemId == ksId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.Payload!.RootElement.TryGetProperty("recommendation", out _)));
    }

    [Fact]
    public async Task Triage_ignores_structural_conflicts()
    {
        var ksId = await SeedWorkspaceAsync("structural");
        await SeedConflictAsync(ksId, "cycle", PayloadWithResolutions("rm-subclass"));
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.9,"reason":"clean merge"}""");

        var log = await RunTriageAsync(ksId, new OnToPilotOptions());

        // Only the predspec conflict went to the LLM — the cycle row is
        // never in AUTO_TYPES.
        Assert.Single(log);
        Assert.Equal(1, _chat.CallCount);

        await using var verify = _dbFactory.CreateDbContext();
        var cycle = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId && c.Ctype == "cycle");
        Assert.False(cycle.Payload!.RootElement.TryGetProperty("recommendation", out _));
        var predspec = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId && c.Ctype == "predicate_specialization");
        Assert.True(predspec.Payload!.RootElement.TryGetProperty("recommendation", out _));
    }

    [Fact]
    public async Task Triage_gate_off_never_calls_the_llm()
    {
        var ksId = await SeedWorkspaceAsync("gate-off");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        var log = await RunTriageAsync(ksId, new OnToPilotOptions { AgenticConflictResolution = false });

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Triage_malformed_reply_then_finish_still_resolves()
    {
        var ksId = await SeedWorkspaceAsync("malformed");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        _chat.Enqueue("not json at all");
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.8,"reason":"recovered"}""");

        var log = await RunTriageAsync(ksId, new OnToPilotOptions());

        // First turn corrected with "Reply with a single JSON object.",
        // second turn finished — the correction message must be visible.
        Assert.Single(log);
        Assert.Equal(2, _chat.CallCount);
        var secondTurn = _chat.CallMessages[1];
        Assert.Equal("Reply with a single JSON object.", secondTurn[^1].Text);

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.True(row.Payload!.RootElement.TryGetProperty("recommendation", out _));
    }

    [Fact]
    public async Task Triage_uses_get_neighborhood_tool_then_finishes()
    {
        var ksId = await SeedWorkspaceAsync("neighborhood");
        var graphIri = $"http://goodcrew.local/ks/neighborhood";
        SeedTBox(graphIri, $"{graphIri}#");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        _chat.Enqueue("""{"action":"get_neighborhood","name":"Pump"}""");
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.85,"reason":"same relation"}""");

        var log = await RunTriageAsync(ksId, new OnToPilotOptions(), graphIri: graphIri);

        Assert.Single(log);
        Assert.Equal(2, _chat.CallCount);

        // Turn 2's final message is the tool result the production loop
        // injected — it must carry the structural context of "Pump".
        var toolResult = _chat.CallMessages[1][^1].Text;
        Assert.StartsWith("get_neighborhood result:", toolResult);
        Assert.Contains("\"label\":\"Pump\"", toolResult);
        Assert.Contains("Equipment", toolResult);        // superclass
        Assert.Contains("Centrifugal Pump", toolResult); // subclass
        Assert.Contains("properties_out", toolResult);   // owns (Station → Pump)
        Assert.Contains("disjoint_with", toolResult);
    }

    [Fact]
    public async Task Triage_extraction_active_is_a_noop()
    {
        var ksId = await SeedWorkspaceAsync("extraction-active");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        await using (var seedDb = _dbFactory.CreateDbContext())
        {
            seedDb.ExtractionJobs.Add(new ExtractionJobEntity
            {
                Id = Guid.NewGuid(),
                LegacyId = TestLegacyIds.Next("extraction_job"),
                KnowledgeSystemId = ksId,
                Kind = "tbox",
                Status = "pending",
                Model = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.9,"reason":"should not run"}""");
        var jobs = new ExtractionJobStore(_dbFactory, TimeProvider.System);

        var log = await RunTriageAsync(ksId, new OnToPilotOptions(), jobs: jobs);

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Triage_missing_llm_provider_is_a_noop()
    {
        // KS without LlmProviderId and no SystemConfig row → the provider
        // resolution throws inside; the agent swallows it (Python catches
        // every LLM-side error and leaves the conflict to a human).
        await using var seedDb = _dbFactory.CreateDbContext();
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = "conflict-agent-no-provider",
            GraphIri = "http://goodcrew.local/ks/no-provider",
            BaseIri = "http://goodcrew.local/ks/no-provider#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.KnowledgeSystems.Add(ks);
        await seedDb.SaveChangesAsync();
        await SeedConflictAsync(ks.Id, "predicate_specialization", PayloadWithResolutions("merge"));

        var log = await RunTriageAsync(ks.Id, new OnToPilotOptions());

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Triage_unbuildable_chat_client_is_a_noop()
    {
        var ksId = await SeedWorkspaceAsync("no-client");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        // Create() throws when no client is installed.
        _chatFactory.UseClient(null);

        var log = await RunTriageAsync(ksId, new OnToPilotOptions());

        Assert.Empty(log);
    }

    [Fact]
    public async Task Triage_truncates_reason_to_200_chars()
    {
        var ksId = await SeedWorkspaceAsync("truncate");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        _chat.Enqueue(JsonSerializer.Serialize(new
        {
            action = "finish",
            resolution = "merge",
            confidence = 0.5,
            reason = new string('x', 250),
        }));

        await RunTriageAsync(ksId, new OnToPilotOptions());

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        var reason = row.Payload!.RootElement
            .GetProperty("recommendation").GetProperty("reason").GetString();
        Assert.Equal(200, reason!.Length);
    }

    [Fact]
    public void ResolveSystemPrompt_uses_chinese_variant_for_zh_cn()
    {
        var agent = new ConflictAgent(
            _chatFactory,
            _dbFactory.CreateDbContext(),
            _store,
            options: Options.Create(new OnToPilotOptions { SystemLanguage = "zh-CN" }));
        Assert.Contains("冲突", agent.ResolveSystemPrompt());
    }

    [Fact]
    public async Task Triage_max_steps_zero_budget_leaves_conflict_untouched()
    {
        // Python `for _ in range(0)` never turns — a zero budget is a no-op.
        var ksId = await SeedWorkspaceAsync("zero-budget");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        var log = await RunTriageAsync(ksId, new OnToPilotOptions { ConflictAgentMaxSteps = 0 });

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> RunTriageAsync(
        Guid ksId,
        OnToPilotOptions options,
        ExtractionJobStore? jobs = null,
        string? graphIri = null)
    {
        if (graphIri is not null)
        {
            // Point the seeded KS at the graph the store actually holds.
            await using var patch = _dbFactory.CreateDbContext();
            var row = await patch.KnowledgeSystems.SingleAsync(k => k.Id == ksId);
            row.GraphIri = graphIri;
            row.BaseIri = $"{graphIri}#";
            await patch.SaveChangesAsync();
        }

        await using var agentDb = _dbFactory.CreateDbContext();
        var agent = new ConflictAgent(
            _chatFactory, agentDb, _store, jobs, Options.Create(options));
        return await agent.TriageAsync(ksId, CancellationToken.None);
    }

    private async Task<Guid> SeedWorkspaceAsync(string tag)
    {
        await using var db = _dbFactory.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = $"conflict-agent-llm-{tag}",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "test-model",
            Kind = "llm",
            ConcurrencyLimit = 10,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = $"conflict-agent-{tag}",
            Description = "Seed KS for ConflictAgent tests.",
            GraphIri = $"http://goodcrew.local/ks/{tag}",
            BaseIri = $"http://goodcrew.local/ks/{tag}#",
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks.Id;
    }

    private async Task SeedConflictAsync(Guid ksId, string ctype, string payloadJson)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.Conflicts.Add(new ConflictEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("conflict"),
            KnowledgeSystemId = ksId,
            Signature = $"{ctype}|{Guid.NewGuid():N}",
            Ctype = ctype,
            Severity = "warning",
            Status = "open",
            Title = $"{ctype} (agent test)",
            Detail = "Seeded for ConflictAgent tests.",
            Payload = JsonDocument.Parse(payloadJson),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string PayloadWithResolutions(params string[] ids)
    {
        var resolutions = ids.Select(id =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["label"] = $"Resolution {id}",
                ["op"] = new Dictionary<string, object?> { ["op"] = "noop" },
            }));
        return $$"""{"entities":[{"iri":"http://goodcrew.local/onto#X","label":"X"}],"resolutions":[{{string.Join(",", resolutions)}}]}""";
    }

    private void SeedTBox(string graphIri, string baseIri)
    {
        var mutation = new OntologyMutation(
            Classes: new[]
            {
                new ClassMutation("Pump"),
                new ClassMutation("Equipment"),
                new ClassMutation("Centrifugal Pump"),
                new ClassMutation("Station"),
            },
            ObjectProperties: new[]
            {
                new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump"),
            },
            DataProperties: Array.Empty<PropertyMutation>(),
            Axioms: new[]
            {
                new AxiomMutation("subclass", Sub: "Pump", Super: "Equipment"),
                new AxiomMutation("subclass", Sub: "Centrifugal Pump", Super: "Pump"),
                new AxiomMutation("disjoint", A: "Pump", B: "Station"),
            });
        var quads = SchemaBuilder.BuildMutation(baseIri, mutation, graphIri);
        _store.AddQuads(new OntoNamedNode(graphIri), quads);
    }
}
