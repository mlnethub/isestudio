using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Unit tests for the P3-1 proposal segment. The happy-path / throw tests
/// run a real <see cref="TerminologyAgent"/> against the shared fake chat
/// factory, so the class joins <see cref="ExtractionTestCollection"/>.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ProposalStepTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/term-step5";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly SqliteContextFactory _contexts = new();
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly FakeChat _chat = new();

    public ProposalStepTests()
    {
        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(_chat);
    }

    [Fact]
    public async Task ExecuteAsync_GatingNotMet_FoldsWithoutProposals()
    {
        using var db = _contexts.CreateDbContext();
        var step = new ProposalStep(
            null,
            db,
            Options.Create(new ISEStudioOptions()),
            NullLogger<ProposalStep>.Instance);
        var ks = new KsContext(GraphIri, BaseIri);

        // Sub-case 1: SuggestEnabled=false — the operator switch is off.
        var r1 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: false),
            new BroaderCarry(new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, TermsAdded: 2)),
            CancellationToken.None);
        Assert.Equal(0, r1.ProposalsQueued);
        Assert.Equal(2, r1.TermsAdded);

        // Sub-case 2: Error carry — the deterministic sync errored.
        var r2 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: true),
            new BroaderCarry(new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, Error: "boom")),
            CancellationToken.None);
        Assert.Equal("boom", r2.Error);
        Assert.Equal(0, r2.ProposalsQueued);

        // Sub-case 3: no SchemeIri — the deterministic sync short-circuited.
        var r3 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: true),
            new BroaderCarry(new TermSyncCarry(null, null, null, 0, TermsAdded: 2)),
            CancellationToken.None);
        Assert.Equal(0, r3.ProposalsQueued);
        Assert.Equal(2, r3.TermsAdded);

        // Gating never reached the chat layer.
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ExecuteAsync_QueuesProposalsFromAgent()
    {
        var chunkId = SeedChunk();
        // The agent's _source_contains grounding check requires the
        // proposal's preferred_label to appear verbatim (ordinal,
        // case-insensitive) in the cited chunk's text, so the canned
        // "Term {i}" labels of FakeChat.EnqueueTerminologyProposal would be
        // rejected against the pump/impeller corpus. Mirror the orchestrator
        // test's grounded pairing (label "Impeller" in this exact text).
        _chat.Enqueue($$"""
            {
              "proposals": [
                {
                  "action": "create",
                  "preferred_label": "Impeller",
                  "language": "en",
                  "alternate_labels": ["alt-0"],
                  "hidden_labels": [],
                  "description": "Auto-suggested term 0",
                  "broader_concept_iri": null,
                  "mapped_entity_iri": null,
                  "confidence": 0.85,
                  "reason": "extracted from chunk 0",
                  "source_chunk_ids": ["{{chunkId}}"]
                }
              ]
            }
            """);

        using var db = _contexts.CreateDbContext();
        var agent = new TerminologyAgent(
            FakeChatClientFactory.Default,
            db,
            Options.Create(new ISEStudioOptions()),
            TimeProvider.System);
        var step = new ProposalStep(
            agent,
            db,
            Options.Create(new ISEStudioOptions { TerminologySuggestionMaxChunks = 10 }),
            NullLogger<ProposalStep>.Instance);

        var result = await step.ExecuteAsync(
            new TerminologyInput(new KsContext(GraphIri, BaseIri), _ksId, "fake-model", SuggestEnabled: true),
            new BroaderCarry(new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, TermsAdded: 2)),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.ProposalsQueued);
        Assert.Equal(2, result.TermsAdded);

        await using var check = _contexts.CreateDbContext();
        var row = Assert.Single(await check.TermProposals.ToListAsync());
        Assert.Equal("Impeller", row.Term);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ExecuteAsync_AgentThrows_Propagates()
    {
        // No client installed in the shared factory → the agent's chat
        // resolution throws. The step must NOT swallow it (P1-4 parity —
        // the orchestrator's outer catch marks the capture).
        SeedChunk();
        FakeChatClientFactory.Default.Reset();

        using var db = _contexts.CreateDbContext();
        var agent = new TerminologyAgent(
            FakeChatClientFactory.Default,
            db,
            Options.Create(new ISEStudioOptions()),
            TimeProvider.System);
        var step = new ProposalStep(
            agent,
            db,
            Options.Create(new ISEStudioOptions()),
            NullLogger<ProposalStep>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            step.ExecuteAsync(
                new TerminologyInput(new KsContext(GraphIri, BaseIri), _ksId, "fake-model", SuggestEnabled: true),
                new BroaderCarry(new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0)),
                CancellationToken.None));
    }

    /// <summary>
    /// Seed the knowledge system + provider + one parsed chunk, so the
    /// step's chunk-id query finds exactly one row (the agent grounds its
    /// proposal against it). Returns the chunk's Guid PK.
    /// </summary>
    private Guid SeedChunk()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = "term-step-llm",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term step fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "pump.txt",
            Folder = "/",
            ParseStatus = "parsed",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        var text = "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        return chunk.Id;
    }

    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        _contexts.Dispose();
    }
}
