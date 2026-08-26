using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Conflicts;

/// <summary>
/// Unit tests for the agentic conflict triage (.NET port of
/// <c>backend/app/ontology/conflict_agent.py</c>). The agent attaches a
/// <c>payload.recommendation</c> to open <c>duplicate</c> /
/// <c>predicate_specialization</c> conflicts after a short ReAct tool loop;
/// decisions at or above <see cref="ISEStudioOptions.AutoApplyFloor"/>
/// auto-apply instead (product decision P3-11 — Python's
/// <c>AUTO_APPLY_TYPES</c> stays empty, so the apply branch is a .NET
/// extension). The agent auto-applies whenever the graph store is wired
/// and the decision confidence is at or above the floor (Phase 2:
/// <c>LegacyIdAllocator</c> retired — the store is the only gate);
/// below-floor decisions keep the recommendation-only behaviour so the
/// P1-1 contract tests stay representative. Structural conflicts are
/// untouched; every LLM hiccup leaves the conflict for a human instead
/// of failing.
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
        _storePath = Path.Combine(Path.GetTempPath(), "isestudio-conflict-agent-" + Guid.NewGuid().ToString("N"));
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

        // Below the AutoApplyFloor (0.85) so the decision lands in the
        // recommendation-only path (Phase 2: the agent auto-applies
        // whenever the graph store is wired and confidence >= floor).
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.8,"reason":"same relation, range noun baked in"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions { SystemLanguage = "en" });

        Assert.Single(log);
        Assert.Contains("recommend", log[0]);

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        var payload = row.Payload!.RootElement;
        // Existing payload keys survive the merge (Python {**c.payload, ...}).
        Assert.True(payload.TryGetProperty("resolutions", out _));
        var rec = payload.GetProperty("recommendation");
        Assert.Equal("merge", rec.GetProperty("resolution_id").GetString());
        Assert.Equal(0.8, rec.GetProperty("confidence").GetDouble());
        Assert.Equal("same relation, range noun baked in", rec.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Triage_parses_string_confidence()
    {
        var ksId = await SeedWorkspaceAsync("conf-string");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        // Python float(str(...)) accepts string confidences; the port must too.
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":"0.75","reason":"ok"}""");

        await RunTriageAsync(ksId, new ISEStudioOptions());

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions());

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

        // Below the AutoApplyFloor (0.85): the predspec decision becomes a
        // recommendation, the row stays open (Phase 2: auto-apply needs a
        // wired graph store and confidence >= floor).
        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.8,"reason":"clean merge"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions());

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions { AgenticConflictResolution = false });

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions());

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri);

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), jobs: jobs);

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

        var log = await RunTriageAsync(ks.Id, new ISEStudioOptions());

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

        var log = await RunTriageAsync(ksId, new ISEStudioOptions());

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

        await RunTriageAsync(ksId, new ISEStudioOptions());

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
            options: Options.Create(new ISEStudioOptions { SystemLanguage = "zh-CN" }));
        Assert.Contains("冲突", agent.ResolveSystemPrompt());
    }

    [Fact]
    public async Task Triage_max_steps_zero_budget_leaves_conflict_untouched()
    {
        // Python `for _ in range(0)` never turns — a zero budget is a no-op.
        var ksId = await SeedWorkspaceAsync("zero-budget");
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        var log = await RunTriageAsync(ksId, new ISEStudioOptions { ConflictAgentMaxSteps = 0 });

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    // ------------------------------------------------------------------
    // P3-11 auto-apply (product decision: decisions at or above the
    // confidence floor apply automatically; below-floor decisions attach a
    // recommendation). Mirrors the (unreachable-in-Python) auto-apply
    // branch of conflict_agent._resolve.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Triage_auto_applies_confident_decision()
    {
        const string tag = "auto-apply";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var baseIri = $"{graphIri}#";
        var ksId = await SeedWorkspaceAsync(tag);
        await SeedConflictWithOpAsync(ksId, "duplicate", "keep-general", "Keep general",
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "AutoClass" });

        _chat.Enqueue("""{"action":"finish","resolution":"keep-general","confidence":0.92,"reason":"confident merge"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        var entry = Assert.Single(log);
        Assert.Contains("auto", entry);

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("resolved", row.Status);
        Assert.Equal("keep-general", row.Resolution);
        Assert.NotNull(row.ResolvedAt);

        // The editor really ran: the TBox graph gained the new class.
        Assert.NotEmpty(_store.Match(
            subjectIri: $"{baseIri}AutoClass", graphIri: graphIri));

        // One audit row: agent actor, conflict.resolve action, agent flag,
        // non-empty TBox diff, Python conflict_id key.
        var audit = await verify.AuditEvents.SingleAsync(e => e.KnowledgeSystemId == ksId);
        Assert.Equal("conflict.resolve", audit.Action);
        Assert.Equal("conflict-agent", audit.ActorName);
        Assert.Null(audit.ActorId);
        Assert.Null(audit.Graph);
        Assert.NotNull(audit.Added);
        var detail = audit.Detail!.RootElement;
        Assert.True(detail.GetProperty("agent").GetBoolean());
        Assert.Equal(row.LegacyId, detail.GetProperty("conflict_id").GetInt64());
        Assert.Equal("keep-general", detail.GetProperty("resolution").GetString());
        Assert.Equal("confident merge", detail.GetProperty("reason").GetString());
        Assert.Equal(0.92, detail.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Triage_floor_boundary_confidence_auto_applies()
    {
        // conf == floor (0.85) satisfies Python's `conf >= floor` check.
        const string tag = "floor-boundary";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var ksId = await SeedWorkspaceAsync(tag);
        await SeedConflictWithOpAsync(ksId, "predicate_specialization", "merge", "Merge",
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "FloorClass" });

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.85,"reason":"at the floor"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Contains("auto", Assert.Single(log));
        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("resolved", row.Status);
        Assert.Single(await verify.AuditEvents.Where(e => e.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Triage_below_floor_attaches_recommendation_instead()
    {
        // conf 0.84 < floor 0.85 → recommendation path, row stays open,
        // no audit rows — even with the allocator wired.
        const string tag = "below-floor";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var ksId = await SeedWorkspaceAsync(tag);
        await SeedConflictWithOpAsync(ksId, "duplicate", "keep-general", "Keep general",
            new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Nope" });

        _chat.Enqueue("""{"action":"finish","resolution":"keep-general","confidence":0.84,"reason":"almost"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Contains("recommend", Assert.Single(log));
        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("open", row.Status);
        Assert.Null(row.Resolution);
        Assert.True(row.Payload!.RootElement.TryGetProperty("recommendation", out _));
        Assert.Empty(await verify.AuditEvents.Where(e => e.KnowledgeSystemId == ksId).ToListAsync());
        // The graph is untouched too.
        Assert.Empty(_store.Match(subjectIri: $"{graphIri}#Nope", graphIri: graphIri));
    }

    [Fact]
    public async Task Triage_apply_failure_leaves_conflict_open_and_unattached()
    {
        // Python catches the apply error, logs a warning and continues —
        // no recommendation, no audit, the row stays open for a human.
        const string tag = "apply-fail";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var ksId = await SeedWorkspaceAsync(tag);
        await SeedConflictWithOpAsync(ksId, "predicate_specialization", "merge", "Merge",
            new Dictionary<string, object?> { ["op"] = "bogus_op" });

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.9,"reason":"confident but broken"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Empty(log);
        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("open", row.Status);
        Assert.Null(row.Resolution);
        Assert.False(row.Payload!.RootElement.TryGetProperty("recommendation", out _));
        Assert.Empty(await verify.AuditEvents.Where(e => e.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Triage_auto_apply_cascades_to_abox_with_grouped_audit()
    {
        // delete_class repoints/retypes instance data: the TBox row and the
        // cascaded ABox row share one GroupId, the ABox row carries the
        // abox graph IRI. Mirrors Python's dual-capture + grouped audit.
        const string tag = "cascade";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var baseIri = $"{graphIri}#";
        var ksId = await SeedWorkspaceAsync(tag);

        SeedTBox(graphIri, baseIri); // classes incl. Pump
        var aboxIri = $"{graphIri}/abox";
        var aboxGraph = new OntoNamedNode(aboxIri);
        var alice = new OntoNamedNode($"{baseIri}alice");
        var pump = new OntoNamedNode($"{baseIri}Pump");
        _store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(alice, Vocabulary.RdfType, pump, aboxGraph),
        });

        await SeedConflictWithOpAsync(ksId, "duplicate", "keep-general", "Keep general",
            new Dictionary<string, object?> { ["op"] = "delete_class", ["iri"] = $"{baseIri}Pump" });

        _chat.Enqueue("""{"action":"finish","resolution":"keep-general","confidence":0.95,"reason":"drop the duplicate"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Contains("auto", Assert.Single(log));

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("resolved", row.Status);

        // The individual lost its only type → removed with its assertions.
        Assert.Empty(_store.Match(graph: aboxGraph));

        // TBox + ABox audit rows share one GroupId; the ABox row names the
        // instance graph and omits the reason (Python parity).
        var audits = await verify.AuditEvents.Where(e => e.KnowledgeSystemId == ksId)
            .OrderBy(e => e.Graph).ToListAsync();
        Assert.Equal(2, audits.Count);
        var tboxAudit = audits.Single(a => a.Graph is null);
        var aboxAudit = audits.Single(a => a.Graph is not null);
        Assert.Equal(aboxIri, aboxAudit.Graph);
        Assert.NotNull(tboxAudit.GroupId);
        Assert.Equal(tboxAudit.GroupId, aboxAudit.GroupId);
        Assert.NotNull(aboxAudit.Removed);
        Assert.Contains("cascaded", aboxAudit.Summary);
        Assert.False(aboxAudit.Detail!.RootElement.TryGetProperty("reason", out _));
        Assert.True(tboxAudit.Detail!.RootElement.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Triage_auto_apply_noop_resolution_resolves_with_empty_diff()
    {
        // A noop resolution (detector hint) skips the editor entirely and
        // resolves with an empty diff — the audit row still records the
        // decision so every auto-apply is auditable.
        const string tag = "noop-apply";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var ksId = await SeedWorkspaceAsync(tag);
        await SeedConflictAsync(ksId, "predicate_specialization", PayloadWithResolutions("merge"));

        _chat.Enqueue("""{"action":"finish","resolution":"merge","confidence":0.9,"reason":"keep as-is"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Contains("auto", Assert.Single(log));
        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId);
        Assert.Equal("resolved", row.Status);
        var audit = await verify.AuditEvents.SingleAsync(e => e.KnowledgeSystemId == ksId);
        Assert.Equal("conflict.resolve", audit.Action);
        Assert.Null(audit.Added);
        Assert.Null(audit.Removed);
    }

    [Fact]
    public async Task Triage_auto_apply_merge_classes_repoints_and_retypes_individuals()
    {
        // P1-1:83 pipeline — ConflictAgent auto-applies a confident
        // merge_classes decision; the editor must repoint the TBox and
        // re-type ABox individuals off the source class.
        const string tag = "merge-classes";
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var baseIri = $"{graphIri}#";
        var ksId = await SeedWorkspaceAsync(tag);
        SeedTBox(graphIri, baseIri); // Pump, Equipment, Centrifugal Pump, Station

        // An individual typed as Centrifugal Pump — must survive and be
        // retyped to Pump after the merge.
        var aboxGraph = new OntoNamedNode($"{graphIri}/abox");
        var alice = new OntoNamedNode($"{baseIri}alice");
        var pump = new OntoNamedNode($"{baseIri}Pump");
        // SchemaBuilder.BuildMutation normalises "Centrifugal Pump" → PascalCase local "CentrifugalPump".
        var cp = new OntoNamedNode($"{baseIri}CentrifugalPump");
        _store.AddQuads(aboxGraph, new[]
        {
            new OntoQuad(alice, Vocabulary.RdfType, cp, aboxGraph),
        });

        // Duplicate conflict: merge CentrifugalPump → Pump.
        await SeedConflictWithOpAsync(ksId, "duplicate", "merge-cp-into-pump", "Merge Centrifugal Pump → Pump",
            new Dictionary<string, object?>
            {
                ["op"] = "merge_classes",
                ["source"] = $"{baseIri}CentrifugalPump",
                ["target"] = $"{baseIri}Pump",
            });

        _chat.Enqueue("""{"action":"finish","resolution":"merge-cp-into-pump","confidence":0.95,"reason":"CP is a specialization"}""");

        var log = await RunTriageAsync(ksId, new ISEStudioOptions(), graphIri: graphIri, autoApply: true);

        Assert.Contains("auto", Assert.Single(log));

        await using var verify = _dbFactory.CreateDbContext();
        var row = await verify.Conflicts.SingleAsync(c => c.KnowledgeSystemId == ksId && c.Ctype == "duplicate");
        Assert.Equal("resolved", row.Status);

        // The source class is gone (its rdf:type + rdfs:label dropped).
        Assert.Empty(_store.Match(subjectIri: $"{baseIri}CentrifugalPump", graphIri: graphIri));

        // alice is now typed as Pump, no longer as CentrifugalPump.
        Assert.Empty(_store.Match(objectIri: cp.Value, graphIri: aboxGraph.Value));
        Assert.Single(_store.Match(objectIri: pump.Value, graphIri: aboxGraph.Value));

        // One audit row carrying the agent flag + merge reason (the
        // TBox row — the ABox cascade's audit row, when present, shares
        // the GroupId and is asserted below if needed).
        var audits = await verify.AuditEvents
            .Where(e => e.KnowledgeSystemId == ksId && e.Action == "conflict.resolve")
            .ToListAsync();
        Assert.NotEmpty(audits);
        Assert.Contains(audits, a => a.Detail!.RootElement.GetProperty("agent").GetBoolean());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> RunTriageAsync(
        Guid ksId,
        ISEStudioOptions options,
        ExtractionJobStore? jobs = null,
        string? graphIri = null,
        bool autoApply = false)
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

    /// <summary>Seed a conflict whose single resolution carries a custom editor op.</summary>
    private async Task SeedConflictWithOpAsync(
        Guid ksId, string ctype, string resolutionId, string resolutionLabel,
        Dictionary<string, object?> op)
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
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["entities"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["iri"] = "http://goodcrew.local/onto#X",
                        ["label"] = "X",
                    },
                },
                ["resolutions"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = resolutionId,
                        ["label"] = resolutionLabel,
                        ["op"] = op,
                    },
                },
            })),
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
