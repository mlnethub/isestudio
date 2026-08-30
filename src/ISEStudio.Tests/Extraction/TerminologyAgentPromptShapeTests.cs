using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Tests.Ontology;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Pin the contract that <see cref="TerminologyAgent.BuildMessages"/> / <see cref="TerminologyAgent.BuildExistingVocabularyBlock"/>
/// exposes the SKOS scheme's existing concepts to the LLM. The production
/// bug fixed by this slice was: the steward prompt told the LLM to "read
/// the current SKOS vocabulary" but no vocabulary data was attached, so
/// the agent cheerfully generated <c>create</c> proposals for labels that
/// already existed in the scheme — the reviewer's accept step then refused
/// every proposal with a 422 duplicate-label error. These tests guard
/// against regressions of that gap.
///
/// <para>Tests run with the agent's chat-factory dependency wired to a
/// <see cref="FakeChatClientFactory"/> so the prompt-shape assertions stay
/// independent of the LLM call itself; <see cref="BuildMessages"/> /
/// <see cref="BuildExistingVocabularyBlock"/> are pure and never touch the
/// chat client.</para>
/// </summary>
public sealed class TerminologyAgentPromptShapeTests
    : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private const string GraphIri = "http://goodcrew.local/ks/test/term-prompt-shape";
    private const string BaseIri = GraphIri + "/onto#";
    private const string SchemeIri = GraphIri + "/vocab#scheme-extracted";

    private readonly TerminologyServiceFixture _fx;
    private readonly SqliteContextFactory _contexts = new();

    public TerminologyAgentPromptShapeTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _contexts.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // BuildExistingVocabularyBlock — covers every branch.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildExistingVocabularyBlock_without_skos_manager_returns_unavailable_stub()
    {
        var agent = BuildAgent(skos: null);
        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        Assert.Contains("EXISTING CONCEPTS IN THIS SCHEME", block);
        Assert.Contains("(vocabulary view unavailable)", block);
    }

    [Fact]
    public void BuildExistingVocabularyBlock_with_empty_scheme_returns_none_stub()
    {
        // SkosManager wired, but the scheme has no concepts yet — the LLM
        // should be told the scheme is empty so it does not assume any
        // labels are pre-existing.
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        var agent = BuildAgent(skos: manager);

        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        Assert.Contains("EXISTING CONCEPTS IN THIS SCHEME", block);
        Assert.Contains("(none — any new concept proposal is acceptable)", block);
    }

    [Fact]
    public void BuildExistingVocabularyBlock_lists_existing_pref_and_alt_labels()
    {
        // Two seeded concepts, each with prefLabel + altLabels — the LLM
        // must see both so it can route duplicate labels to add_alias
        // instead of create.
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "操作类",
            Language: "zh-CN",
            AltLabels: new[] { new SkosLabel("操作", "zh-CN") },
            MappedEntityIri: null));
        manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "泵站",
            Language: "zh-CN",
            MappedEntityIri: null));

        var agent = BuildAgent(skos: manager);
        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        Assert.Contains("do NOT propose create for these labels; use add_alias instead", block);
        Assert.Contains("prefLabel | altLabels", block);
        Assert.Contains("操作类 | 操作", block);
        Assert.Contains("泵站", block);
    }

    [Fact]
    public void BuildExistingVocabularyBlock_omits_alt_label_column_when_no_aliases()
    {
        // Single concept with no altLabels — the pipe-separator column
        // collapses so the row reads as the pref label only.
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "Pump",
            Language: "en",
            MappedEntityIri: null));

        var agent = BuildAgent(skos: manager);
        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        // The block ends with "Pump" (no trailing pipe separator and no
        // trailing newline since the block is the last segment of the user
        // message). The presence of "\nPump" at end-of-block confirms the
        // alt-label column was suppressed.
        Assert.EndsWith("Pump", block);
        Assert.Contains("prefLabel | altLabels\n", block);
        Assert.DoesNotContain("Pump |", block);
    }

    [Fact]
    public void BuildExistingVocabularyBlock_filters_by_scheme_iri()
    {
        // Two schemes in the same store; only the concepts attached to
        // the requested scheme should surface.
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        var otherSchemeIri = GraphIri + "/vocab#scheme-other";
        SeedScheme(manager, otherSchemeIri, title: "Other scheme");
        manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "Owned",
            Language: "en",
            MappedEntityIri: null));
        manager.CreateConcept(Ks, otherSchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "Foreign",
            Language: "en",
            MappedEntityIri: null));

        var agent = BuildAgent(skos: manager);
        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        Assert.Contains("Owned", block);
        Assert.DoesNotContain("Foreign", block);
    }

    [Fact]
    public void BuildExistingVocabularyBlock_shows_alphabetical_sample_when_over_max_existing()
    {
        // Beyond the 200-row cap the agent cannot render every concept
        // (token budget). The block falls back to a deterministic
        // alphabetical sample (first 80 of 247) plus a count note
        // instructing the LLM to verify against the sample AND the
        // source chunks before proposing create. Without the sample,
        // the LLM is flying blind and may confidently invent duplicates.
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        for (var i = 0; i < 247; i++)
        {
            manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
                Iri: "",
                PrefLabel: $"Term {i:D3}",
                Language: "en",
                MappedEntityIri: null));
        }

        var agent = BuildAgent(skos: manager);
        var block = agent.BuildExistingVocabularyBlock(MakeKnowledgeSystem(), SchemeIri);

        Assert.Contains("a sample of 80 of 247 concepts is shown", block);
        Assert.Contains("the remaining 167 are not listed", block);
        Assert.Contains("Do NOT propose create for any label you cannot verify", block);
        Assert.Contains("If unsure, prefer add_alias over create.", block);
        Assert.Contains("prefLabel | altLabels", block);
        // The first 80 prefLabels (alphabetical) are present, the rest are not.
        Assert.Contains("Term 000", block);
        Assert.Contains("Term 079", block);
        Assert.DoesNotContain("Term 080", block);
        Assert.DoesNotContain("Term 246", block);
    }

    // ------------------------------------------------------------------
    // BuildMessages — covers the integration with the user-message
    // template: scheme IRI line + existing-vocab block + source chunks
    // + the steward prompt's closing line.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildMessages_user_message_carries_scheme_iri_and_existing_concepts()
    {
        var manager = new SkosManager(_fx.Store);
        SeedScheme(manager);
        manager.CreateConcept(Ks, SchemeIri, new SkosConceptData(
            Iri: "",
            PrefLabel: "操作类",
            Language: "zh-CN",
            MappedEntityIri: null));

        var agent = BuildAgent(skos: manager);
        var chunkId = Guid.NewGuid();
        var chunks = MakeChunks((chunkId, "hello world"));

        var messages = agent.BuildMessages(MakeKnowledgeSystem(), SchemeIri, chunks);

        var user = messages.Last(m => m.Role == ChatRole.User);
        Assert.Contains("CURRENT CONTROLLED TERMS SCHEME:", user.Text);
        Assert.Contains(SchemeIri, user.Text);
        Assert.Contains("EXISTING CONCEPTS IN THIS SCHEME", user.Text);
        Assert.Contains("操作类", user.Text);
        Assert.Contains("[chunk:" + chunkId + "]", user.Text);
        Assert.Contains("Propose controlled-terminology changes.", user.Text);
    }

    [Fact]
    public void BuildMessages_user_message_does_not_attach_existing_concepts_when_skos_unavailable()
    {
        // No SkosManager wired — the prompt falls back to the (unavailable)
        // stub but still includes scheme IRI + source chunks. This is the
        // shape tests run with when DI skips SkosManager (the production
        // build wires it; this guards the hand-built test path from
        // regressing to a silently-empty block).
        var agent = BuildAgent(skos: null);
        var chunks = MakeChunks((Guid.NewGuid(), "body"));

        var messages = agent.BuildMessages(MakeKnowledgeSystem(), SchemeIri, chunks);

        var user = messages.Last(m => m.Role == ChatRole.User);
        Assert.Contains("(vocabulary view unavailable)", user.Text);
        Assert.DoesNotContain("do NOT propose create for these labels", user.Text);
    }

    [Fact]
    public void BuildMessages_returns_system_prompt_then_user_prompt()
    {
        var agent = BuildAgent(skos: null);
        var chunks = MakeChunks((Guid.NewGuid(), "body"));

        var messages = agent.BuildMessages(MakeKnowledgeSystem(), SchemeIri, chunks);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        // System prompt is resolved through PromptLocales — non-empty so
        // the LLM has steward guidance.
        Assert.False(string.IsNullOrWhiteSpace(messages[0].Text));
    }

    [Fact]
    public void BuildMessages_truncates_chunk_excerpts_to_2000_chars()
    {
        var agent = BuildAgent(skos: null);
        var longText = new string('x', 5_000);
        var chunkId = Guid.NewGuid();
        var chunks = MakeChunks((chunkId, longText));

        var messages = agent.BuildMessages(MakeKnowledgeSystem(), SchemeIri, chunks);
        var user = messages.Last(m => m.Role == ChatRole.User);

        // Body contains exactly 2000 'x' after the [chunk:...] header —
        // proves the truncation in BuildMessages (line 377) is honoured.
        Assert.Contains(new string('x', 2000), user.Text);
        Assert.DoesNotContain(new string('x', 2001), user.Text);
        Assert.Contains("[chunk:" + chunkId + "]", user.Text);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static readonly KsContext Ks = new(GraphIri, BaseIri, Name: "Prompt shape fixture");

    private TerminologyAgent BuildAgent(SkosManager? skos)
    {
        // BuildMessages never calls the chat factory, so wiring
        // FakeChatClientFactory.Default is fine even if its slot is empty.
        // Using Options.Create(new ISEStudioOptions()) mirrors the production
        // agent — the prompt-key lookup is language-driven via the SystemLanguage
        // default.
        return new TerminologyAgent(
            chatFactory: FakeChatClientFactory.Default,
            db: _contexts.CreateDbContext(),
            options: Options.Create(new ISEStudioOptions()),
            clock: TimeProvider.System,
            skos: skos);
    }

    private static KnowledgeSystemEntity MakeKnowledgeSystem() => new()
    {
        Id = Guid.NewGuid(),
        PublicId = Guid.NewGuid().ToString("N"),
        Name = "Prompt shape fixture",
        GraphIri = GraphIri,
        BaseIri = BaseIri,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static IReadOnlyDictionary<Guid, ChunkEntity> MakeChunks(
        params (Guid Id, string Text)[] entries)
    {
        var dict = new Dictionary<Guid, ChunkEntity>();
        foreach (var (id, text) in entries)
        {
            dict[id] = new ChunkEntity
            {
                Id = id,
                DocumentId = Guid.NewGuid(),
                Idx = 0,
                Text = text,
                CharStart = 0,
                CharEnd = text.Length,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
        return dict;
    }

    private void SeedScheme(SkosManager manager) =>
        SeedScheme(manager, SchemeIri, title: "Prompt shape fixture scheme");

    private void SeedScheme(SkosManager manager, string iri, string title)
    {
        manager.CreateScheme(Ks, new SkosSchemeData(
            Iri: iri,
            Title: title,
            DefaultLanguage: "en",
            Origin: "test"));
    }
}