using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Unit tests for the agentic structure repair (.NET port of
/// <c>backend/app/ontology/structure_agent.py</c>). The agent proposes a
/// broader parent for each isolated class (no parent, no children, no
/// property usage), then auto-attaches the confident, source-grounded,
/// lexically safe suggestions as <c>subclass_of</c> — creating the parent
/// first when the model proposed a new one. Over-general catch-alls,
/// ungrounded evidence, and every LLM hiccup leave the class for a human.
///
/// <para>Each test owns a <see cref="FakeChat"/> instance (fresh per test
/// method via xUnit's per-test class construction), so the tests run in
/// parallel without shared chat state. The DB is a shared-cache SQLite
/// database through <see cref="SqliteContextFactory"/>; the Oxigraph store
/// is a per-instance RocksDB temp directory. Providers are seeded with
/// <c>ConcurrencyLimit = 1</c> so the Pass-1 fan-out runs the proposals in
/// deterministic class-label order.</para>
/// </summary>
public sealed class StructureAgentTests : IDisposable
{
    private readonly SqliteContextFactory _dbFactory = new();
    private readonly string _storePath;
    private readonly StoreWrapper _store;
    private readonly FakeChat _chat = new();
    private readonly FakeChatClientFactory _chatFactory = new();

    public StructureAgentTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "isestudio-structure-agent-" + Guid.NewGuid().ToString("N"));
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
    public async Task Attach_adds_subclass_for_existing_parent()
    {
        var ksId = await SeedWorkspaceAsync("existing");
        SeedTBox("existing",
            new[] { new ClassMutation("Centrifugal Pump"), new ClassMutation("Pump"), new ClassMutation("Station") },
            new[] { new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump") },
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId,
            "The centrifugal pump is a kind of pump used at the station.");

        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"The centrifugal pump is a kind of pump","reason":"explicit is-a in source"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(new[] { "Centrifugal Pump ⊑ Pump (auto 0.90)" }, log);
        Assert.Equal(1, _chat.CallCount);

        var view = SchemaBuilder.BuildView("http://goodcrew.local/ks/existing", _store);
        var pump = view.Classes.Single(c => c.Label == "Pump");
        var centrifugal = view.Classes.Single(c => c.Label == "Centrifugal Pump");
        Assert.Contains(pump.Iri, centrifugal.Superclasses);

        await using var verify = _dbFactory.CreateDbContext();
        var audit = await verify.AuditEvents.SingleAsync(a => a.KnowledgeSystemId == ksId);
        Assert.Equal("tbox.attach_isolated", audit.Action);
        Assert.Equal("structure-agent", audit.ActorName);
        Assert.Null(audit.ActorId);
        Assert.Equal("Agent attached \"Centrifugal Pump\" ⊑ \"Pump\"", audit.Summary);
        var detail = audit.Detail!.RootElement;
        Assert.Equal(centrifugal.Iri, detail.GetProperty("class").GetString());
        Assert.Equal("Pump", detail.GetProperty("parent").GetString());
        Assert.False(detail.GetProperty("new").GetBoolean());
        Assert.True(detail.GetProperty("agent").GetBoolean());
        Assert.NotNull(audit.Added);
    }

    [Fact]
    public async Task Attach_creates_new_parent_class_then_attaches()
    {
        var ksId = await SeedWorkspaceAsync("new-parent");
        SeedTBox("new-parent",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "A centrifugal pump is a kind of pump.");

        _chat.Enqueue("""{"parent":"Pump","new":true,"confidence":0.9,"evidence":"A centrifugal pump is a kind of pump","reason":"source states the is-a"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(new[] { "Centrifugal Pump ⊑ Pump (new) (auto 0.90)" }, log);

        var view = SchemaBuilder.BuildView("http://goodcrew.local/ks/new-parent", _store);
        var pump = view.Classes.Single(c => c.Label == "Pump");
        var centrifugal = view.Classes.Single(c => c.Label == "Centrifugal Pump");
        Assert.Contains(pump.Iri, centrifugal.Superclasses);

        await using var verify = _dbFactory.CreateDbContext();
        var audit = await verify.AuditEvents.SingleAsync(a => a.KnowledgeSystemId == ksId);
        Assert.True(audit.Detail!.RootElement.GetProperty("new").GetBoolean());
        Assert.Equal("Agent attached \"Centrifugal Pump\" ⊑ \"Pump\" (new class)", audit.Summary);
    }

    [Fact]
    public async Task Low_confidence_suggestion_is_left_for_a_human()
    {
        var ksId = await SeedWorkspaceAsync("low-conf");
        SeedTBox("low-conf",
            new[] { new ClassMutation("Centrifugal Pump"), new ClassMutation("Pump"), new ClassMutation("Station") },
            new[] { new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump") },
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "The centrifugal pump is a kind of pump.");

        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.5,"evidence":"The centrifugal pump is a kind of pump","reason":"unsure"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(new[] { "Centrifugal Pump: agent suggested \"Pump\" (0.50) — left" }, log);

        var view = SchemaBuilder.BuildView("http://goodcrew.local/ks/low-conf", _store);
        Assert.Empty(view.Classes.Single(c => c.Label == "Centrifugal Pump").Superclasses);
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Ungrounded_evidence_is_left()
    {
        var ksId = await SeedWorkspaceAsync("ungrounded");
        SeedTBox("ungrounded",
            new[] { new ClassMutation("Centrifugal Pump"), new ClassMutation("Pump"), new ClassMutation("Station") },
            new[] { new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump") },
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "The centrifugal pump is a kind of pump.");

        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"hallucinated span absent from source","reason":"made up"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(new[] { "Centrifugal Pump: \"Pump\" was not verified by source evidence — left" }, log);
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Over_general_catch_all_is_left()
    {
        var ksId = await SeedWorkspaceAsync("catch-all");
        SeedTBox("catch-all",
            new[]
            {
                new ClassMutation("Centrifugal Pump"),
                new ClassMutation("Axial Pump"),
                new ClassMutation("Pump"),
                new ClassMutation("Station"),
            },
            new[] { new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump") },
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId,
            "A centrifugal pump is a kind of pump. An axial pump is a kind of pump.");

        // Both isolated classes propose the same parent — a suspicious
        // catch-all (StructureMaxSameParent = 1) → both left for a human.
        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"An axial pump is a kind of pump","reason":"is-a"}""");
        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"A centrifugal pump is a kind of pump","reason":"is-a"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions { StructureMaxSameParent = 1 });

        Assert.Equal(2, log.Count);
        Assert.All(log, l => Assert.EndsWith("classes — likely over-generalization, left", l));
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task No_isolated_classes_never_calls_the_llm()
    {
        var ksId = await SeedWorkspaceAsync("connected");
        SeedTBox("connected",
            new[] { new ClassMutation("Pump"), new ClassMutation("Station") },
            new[] { new PropertyMutation("owns", "object", Domain: "Station", Range: "Pump") },
            new[] { new AxiomMutation("subclass", Sub: "Pump", Super: "Equipment") });

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Gate_off_never_calls_the_llm()
    {
        var ksId = await SeedWorkspaceAsync("gate-off");
        SeedTBox("gate-off",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());

        var log = await RunAttachAsync(ksId, new ISEStudioOptions { AgenticIsolatedClasses = false });

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Extraction_active_is_a_noop()
    {
        var ksId = await SeedWorkspaceAsync("extraction-active");
        SeedTBox("extraction-active",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "A centrifugal pump is a kind of pump.");

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

        _chat.Enqueue("""{"parent":"Pump","new":true,"confidence":0.9,"evidence":"A centrifugal pump is a kind of pump","reason":"should not run"}""");
        var jobs = new ExtractionJobStore(_dbFactory, TimeProvider.System);

        var log = await RunAttachAsync(ksId, new ISEStudioOptions(), jobs);

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Missing_llm_provider_is_a_noop()
    {
        // KS without LlmProviderId and no SystemConfig row → the provider
        // resolution throws inside; the agent swallows it (Python catches
        // every LLM-side error and leaves the class to a human).
        await using var seedDb = _dbFactory.CreateDbContext();
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = "structure-agent-no-provider",
            GraphIri = "http://goodcrew.local/ks/no-provider",
            BaseIri = "http://goodcrew.local/ks/no-provider#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        seedDb.KnowledgeSystems.Add(ks);
        await seedDb.SaveChangesAsync();
        SeedTBox("no-provider",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());

        var log = await RunAttachAsync(ks.Id, new ISEStudioOptions());

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    public async Task Unbuildable_chat_client_is_a_noop()
    {
        var ksId = await SeedWorkspaceAsync("no-client");
        SeedTBox("no-client",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "A centrifugal pump is a kind of pump.");

        // Create() throws when no client is installed.
        _chatFactory.UseClient(null);

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Empty(log);
    }

    [Fact]
    public async Task Malformed_reply_leaves_class_untouched()
    {
        var ksId = await SeedWorkspaceAsync("malformed");
        SeedTBox("malformed",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "A centrifugal pump is a kind of pump.");

        _chat.Enqueue("not json at all");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Empty(log);
        Assert.Equal(1, _chat.CallCount);
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Non_existing_parent_without_new_flag_is_skipped_silently()
    {
        // Python `if not p_iri and not d["new"]: continue` — the agent named
        // a non-existent "existing" class; don't invent it, don't even log.
        var ksId = await SeedWorkspaceAsync("invented-existing");
        SeedTBox("invented-existing",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "The centrifugal pump is a kind of pump.");

        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"The centrifugal pump is a kind of pump","reason":"is-a"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Empty(log);
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
        var view = SchemaBuilder.BuildView("http://goodcrew.local/ks/invented-existing", _store);
        Assert.Single(view.Classes);
    }

    [Fact]
    public async Task Second_proposal_reuses_the_parent_created_by_the_first()
    {
        var ksId = await SeedWorkspaceAsync("index-reuse");
        SeedTBox("index-reuse",
            new[] { new ClassMutation("Centrifugal Pump"), new ClassMutation("Axial Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId,
            "A centrifugal pump is a kind of pump. An axial pump is a kind of pump.");

        // Class-label order puts "Axial Pump" first (ConcurrencyLimit = 1).
        _chat.Enqueue("""{"parent":"Pump","new":true,"confidence":0.9,"evidence":"An axial pump is a kind of pump","reason":"is-a"}""");
        _chat.Enqueue("""{"parent":"Pump","new":false,"confidence":0.9,"evidence":"A centrifugal pump is a kind of pump","reason":"is-a"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(2, log.Count);
        Assert.Contains("Axial Pump ⊑ Pump (new) (auto 0.90)", log[0]);
        Assert.Contains("Centrifugal Pump ⊑ Pump (auto 0.90)", log[1]);

        // Exactly one Pump class was created; both attaches target its IRI.
        var view = SchemaBuilder.BuildView("http://goodcrew.local/ks/index-reuse", _store);
        var pumps = view.Classes.Where(c => c.Label == "Pump").ToList();
        Assert.Single(pumps);
        var pumpIri = pumps[0].Iri;
        Assert.Contains(pumpIri, view.Classes.Single(c => c.Label == "Axial Pump").Superclasses);
        Assert.Contains(pumpIri, view.Classes.Single(c => c.Label == "Centrifugal Pump").Superclasses);
    }

    [Fact]
    public async Task Parent_equal_to_the_class_label_is_left()
    {
        var ksId = await SeedWorkspaceAsync("self-parent");
        SeedTBox("self-parent",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "A centrifugal pump is a kind of pump.");

        _chat.Enqueue("""{"parent":"Centrifugal Pump","new":false,"confidence":0.9,"evidence":"A centrifugal pump is a kind of pump","reason":"nonsense"}""");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Equal(new[] { "Centrifugal Pump: agent suggested \"Centrifugal Pump\" (0.90) — left" }, log);
        await using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(await verify.AuditEvents.Where(a => a.KnowledgeSystemId == ksId).ToListAsync());
    }

    [Fact]
    public async Task Truncates_reason_to_200_chars()
    {
        var ksId = await SeedWorkspaceAsync("truncate");
        SeedTBox("truncate",
            new[] { new ClassMutation("Centrifugal Pump"), new ClassMutation("Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "The centrifugal pump is a kind of pump.");

        _chat.Enqueue(JsonSerializer.Serialize(new
        {
            parent = "Pump",
            @new = false,
            confidence = 0.9,
            evidence = "The centrifugal pump is a kind of pump",
            reason = new string('x', 250),
        }));

        await RunAttachAsync(ksId, new ISEStudioOptions());

        await using var verify = _dbFactory.CreateDbContext();
        var audit = await verify.AuditEvents.SingleAsync(a => a.KnowledgeSystemId == ksId);
        Assert.Equal(200, audit.Detail!.RootElement.GetProperty("reason").GetString()!.Length);
    }

    [Fact]
    public void ResolveSystemPrompt_uses_chinese_variant_for_zh_cn()
    {
        using var db = _dbFactory.CreateDbContext();
        var agent = new StructureAgent(
            _chatFactory,
            db,
            _store,
            options: Options.Create(new ISEStudioOptions { SystemLanguage = "zh-CN" }));
        Assert.Contains("未连接", agent.ResolveSystemPrompt());
    }

    [Fact]
    public async Task Empty_source_text_never_calls_the_llm()
    {
        // Python _decide returns None when source_for yields nothing — no
        // chunk surfaces the class label.
        var ksId = await SeedWorkspaceAsync("no-source");
        SeedTBox("no-source",
            new[] { new ClassMutation("Centrifugal Pump") },
            Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        await SeedChunkAsync(ksId, "Unrelated text about a different topic.");

        var log = await RunAttachAsync(ksId, new ISEStudioOptions());

        Assert.Empty(log);
        Assert.Equal(0, _chat.CallCount);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> RunAttachAsync(
        Guid ksId,
        ISEStudioOptions options,
        ExtractionJobStore? jobs = null)
    {
        await using var agentDb = _dbFactory.CreateDbContext();
        var allocator = new LegacyIdAllocator(agentDb);
        var agent = new StructureAgent(
            _chatFactory, agentDb, _store, jobs, allocator, Options.Create(options));
        return await agent.AttachIsolatedAsync(ksId, model: null, CancellationToken.None);
    }

    private async Task<Guid> SeedWorkspaceAsync(string tag)
    {
        await using var db = _dbFactory.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("provider"),
            Name = $"structure-agent-llm-{tag}",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "test-model",
            Kind = "llm",
            // Keep the Pass-1 fan-out deterministic: proposals run in
            // class-label order so queued FakeChat replies line up.
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("knowledge_system"),
            Name = $"structure-agent-{tag}",
            Description = "Seed KS for StructureAgent tests.",
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

    private async Task SeedChunkAsync(Guid ksId, string text)
    {
        await using var db = _dbFactory.CreateDbContext();
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "source.txt",
            Folder = "/",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("chunk"),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private void SeedTBox(
        string tag,
        IReadOnlyList<ClassMutation> classes,
        IReadOnlyList<PropertyMutation> objectProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var graphIri = $"http://goodcrew.local/ks/{tag}";
        var mutation = new OntologyMutation(
            Classes: classes,
            ObjectProperties: objectProperties,
            DataProperties: Array.Empty<PropertyMutation>(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation($"{graphIri}#", mutation, graphIri);
        _store.AddQuads(new OntoNamedNode(graphIri), quads);
    }
}
