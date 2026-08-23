using System.Text;
using System.Text.Json;
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
/// Tests for the per-chunk TBox verify pipeline (Python
/// <c>_verify_tbox_candidates</c>): the decision-application helpers are
/// exercised without an LLM (they are the fail-closed boundary), the
/// critic → adjudicator → denotation chain runs against a canned
/// <see cref="FakeChat"/>, and one end-to-end test proves the orchestrator
/// wires verification into a real extraction run.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class TBoxVerifyServiceTests : IDisposable
{
    /// <summary>
    /// Source text for the pipeline tests: contains every label the critic
    /// replies quote as evidence (<see cref="FakeChat.VerifySourceText"/>).
    /// </summary>
    private const string Text = FakeChat.VerifySourceText;

    private static readonly TBoxDelta Delta = ExtractionDeltaParser.ParseTBox(FakeChat.ValidTBoxDelta);

    private static readonly TBoxVerifyService Service =
        new(Options.Create(new OnToPilotOptions()));

    // ------------------------------------------------------------------
    // ApplyTBoxRoleDecisions — decision application (no LLM)
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyTBoxRoleDecisions_accepts_grounded_type_decisions()
    {
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, Delta, Payload(FakeChat.VerifyCriticAcceptAll), 0.85);

        Assert.Equal(3, result.Delta.Classes.Count);
        Assert.All(result.Delta.Classes, c => Assert.True(c.RoleVerified));
        Assert.Equal(new[] { "Animal", "Dog", "Collar" },
            result.Delta.Classes.Select(c => c.Label));
        Assert.Empty(result.Rejections);

        // The subclass edge survives and the disjoint_with axiom passes
        // through untouched (Python's {**ontology, ...} spread).
        Assert.Contains(result.Delta.Axioms, a =>
            a.Type == "subclass" && a.Sub == "Dog" && a.Super == "Animal");
        Assert.Contains(result.Delta.Axioms, a =>
            a.Type == "disjoint" && a.A == "Dog" && a.B == "Collar");

        // Properties are never subject to role decisions.
        Assert.Equal(2, result.Delta.ObjectProperties.Count);
        Assert.Equal(2, result.Delta.DataProperties.Count);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_rejects_string_true_keep()
    {
        // Python's `decision.get("keep") is True` is an identity check —
        // the string "true" never satisfies it.
        const string payload = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": "true", "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "role": "type", "keep": "true", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"},
                {"label": "Collar", "role": "type", "keep": "true", "confidence": 0.95,
                 "evidence": "A Collar is worn by a Dog"}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, Delta, Payload(payload), 0.85);

        Assert.Empty(result.Delta.Classes);
        Assert.Equal(3, result.Rejections.Count);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_uses_decision_reason_when_present()
    {
        const string payload = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species",
                 "reason": "used as a generic noun, not a category"}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, new TBoxDelta(
                new[] { new ClassMutation("Animal", "A living creature") },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(payload), 0.85);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal("used as a generic noun, not a category", rejection.Reason);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_rejects_label_not_in_source()
    {
        const string payload = """
            {
              "class_decisions": [
                {"label": "Phantom", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, new TBoxDelta(
                new[] { new ClassMutation("Phantom", null) },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(payload), 0.85);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal("class label is not lexically grounded in the source", rejection.Reason);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_rejects_ungrounded_evidence()
    {
        const string payload = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "invented text that never appears in the source"}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, new TBoxDelta(
                new[] { new ClassMutation("Animal", null) },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(payload), 0.85);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal("missing or ungrounded independent type decision", rejection.Reason);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_rejects_confidence_below_floor()
    {
        const string payload = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": true, "confidence": 0.84,
                 "evidence": "The Animal kingdom has many species"}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, new TBoxDelta(
                new[] { new ClassMutation("Animal", null) },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(payload), 0.85);

        Assert.Empty(result.Delta.Classes);
        Assert.Single(result.Rejections);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_exact_scalar_value_needs_independent_type_evidence()
    {
        // The source declares `"name": "Dog"` as a structured scalar value,
        // so Dog carries an exact non-type role. An evidence span that only
        // re-quotes that same scalar pair cannot count as independent.
        const string text = """
            The entity has a "name": "Dog" property.
            """;
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "\"name\": \"Dog\""}
              ]
            }
            """;
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            text, new TBoxDelta(
                new[] { new ClassMutation("Dog", null) },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(payload), 0.85);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal("exact structured scalar value is not declared as a type", rejection.Reason);

        // The same candidate passes when the evidence quotes a span that is
        // not itself a structured scalar assignment.
        const string independent = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "The entity has a"}
              ]
            }
            """;
        var accepted = TBoxVerifyService.ApplyTBoxRoleDecisions(
            text, new TBoxDelta(
                new[] { new ClassMutation("Dog", null) },
                Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>()),
            Payload(independent), 0.85);
        Assert.Single(accepted.Delta.Classes);
    }

    [Fact]
    public void ApplyTBoxRoleDecisions_subclass_edge_needs_its_own_decision()
    {
        // No subclass_decisions at all: the Dog⊑Animal edge is dropped while
        // the disjoint_with axiom still passes through.
        var result = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, Delta, Payload(FakeChat.VerifyCriticAcceptAll), 0.85);
        Assert.Contains(result.Delta.Axioms, a => a.Type == "subclass");

        const string noSubclass = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"},
                {"label": "Collar", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Collar is worn by a Dog"}
              ]
            }
            """;
        var withoutEdge = TBoxVerifyService.ApplyTBoxRoleDecisions(
            Text, Delta, Payload(noSubclass), 0.85);
        Assert.DoesNotContain(withoutEdge.Delta.Axioms, a => a.Type == "subclass");
        Assert.Contains(withoutEdge.Delta.Axioms, a => a.Type == "disjoint");
    }

    // ------------------------------------------------------------------
    // RemoveRejectedClassReferences
    // ------------------------------------------------------------------

    [Fact]
    public void RemoveRejectedClassReferences_strips_rejected_labels_everywhere()
    {
        var rejected = new HashSet<string>(StringComparer.Ordinal) { "animal", "dog" };
        var cleaned = TBoxVerifyService.RemoveRejectedClassReferences(Delta, rejected);

        // owns/trains have range=Animal and domain=Person: the range is
        // cleared, the untouched domain survives.
        Assert.All(cleaned.ObjectProperties, p => Assert.Null(p.Range));
        Assert.Equal(new[] { "Person", "Person" },
            cleaned.ObjectProperties.Select(p => p.Domain));
        // weightKg/breed have rejected domains: cleared.
        Assert.All(cleaned.DataProperties, p => Assert.Null(p.Domain));
        // subclass(Dog,Animal) and disjoint(Dog,Collar) reference rejected
        // labels and are dropped entirely.
        Assert.Empty(cleaned.Axioms);
    }

    [Fact]
    public void RemoveRejectedClassReferences_is_a_noop_without_rejections()
    {
        var cleaned = TBoxVerifyService.RemoveRejectedClassReferences(
            Delta, new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(Delta, cleaned);
    }

    // ------------------------------------------------------------------
    // DenotationReplacements
    // ------------------------------------------------------------------

    [Fact]
    public void DenotationReplacements_accepts_exact_suffix_replacement()
    {
        const string text = "Sir Dog is a titled animal. A Dog is an Animal.";
        var original = new Dictionary<string, ClassMutation>(StringComparer.Ordinal)
        {
            ["sir dog"] = new ClassMutation("Sir Dog", null),
        };
        var rejected = new HashSet<string>(StringComparer.Ordinal) { "sir dog" };
        const string payload = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "Sir Dog is a titled animal"}
              ],
              "replacement_classes": [
                {"from": "Sir Dog", "label": "Dog", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var replacements = TBoxVerifyService.DenotationReplacements(
            text, Payload(payload), original, rejected, 0.85);

        var replacement = Assert.Single(replacements);
        Assert.Equal("Dog", replacement.Label);
        Assert.True(replacement.RoleVerified);
    }

    [Fact]
    public void DenotationReplacements_requires_rejected_individual_decision()
    {
        const string text = "Sir Dog is a titled animal. A Dog is an Animal.";
        var original = new Dictionary<string, ClassMutation>(StringComparer.Ordinal)
        {
            ["sir dog"] = new ClassMutation("Sir Dog", null),
        };
        var rejected = new HashSet<string>(StringComparer.Ordinal) { "sir dog" };
        // keep=true: the critic kept the source label, so no replacement may
        // be minted for it.
        const string kept = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "Sir Dog is a titled animal"}
              ],
              "replacement_classes": [
                {"from": "Sir Dog", "label": "Dog", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var replacements = TBoxVerifyService.DenotationReplacements(
            text, Payload(kept), original, rejected, 0.85);
        Assert.Empty(replacements);

        // role=type (keep=false): the rejected row is a type candidate, not
        // an individual — suffix recovery is only for proper names.
        const string typeRole = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "type", "keep": false, "confidence": 0.95,
                 "evidence": "Sir Dog is a titled animal"}
              ],
              "replacement_classes": [
                {"from": "Sir Dog", "label": "Dog", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var typeReplacements = TBoxVerifyService.DenotationReplacements(
            text, Payload(typeRole), original, rejected, 0.85);
        Assert.Empty(typeReplacements);
    }

    [Fact]
    public void DenotationReplacements_requires_space_separated_suffix()
    {
        const string text = "Sir Dog is a titled animal. A Puppy is a young Dog.";
        var original = new Dictionary<string, ClassMutation>(StringComparer.Ordinal)
        {
            ["sir dog"] = new ClassMutation("Sir Dog", null),
        };
        var rejected = new HashSet<string>(StringComparer.Ordinal) { "sir dog" };
        // "Puppy" is not a space-separated suffix of "Sir Dog".
        const string payload = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "Sir Dog is a titled animal"}
              ],
              "replacement_classes": [
                {"from": "Sir Dog", "label": "Puppy", "confidence": 0.95,
                 "evidence": "A Puppy is a young Dog"}
              ]
            }
            """;
        var replacements = TBoxVerifyService.DenotationReplacements(
            text, Payload(payload), original, rejected, 0.85);
        Assert.Empty(replacements);
    }

    // ------------------------------------------------------------------
    // VerifyAsync pipeline (FakeChat)
    // ------------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_skips_the_llm_when_there_are_no_candidates()
    {
        var chat = new FakeChat();
        var result = await Service.VerifyAsync(chat, Text, TBoxDelta.Empty, CancellationToken.None);

        Assert.Equal(TBoxDelta.Empty, result.Delta);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task VerifyAsync_critic_then_denotation_accepts_everything()
    {
        var chat = new FakeChat()
            .Enqueue(FakeChat.VerifyCriticAcceptAll)
            .Enqueue(FakeChat.VerifyDenotationAcceptAll);

        var result = await Service.VerifyAsync(chat, Text, Delta, CancellationToken.None);

        Assert.Equal(3, result.Delta.Classes.Count);
        Assert.Contains(result.Delta.Axioms, a => a.Type == "subclass");
        Assert.Contains(result.Delta.Axioms, a => a.Type == "disjoint");
        Assert.Equal(2, result.Delta.ObjectProperties.Count);
        Assert.Empty(result.Rejections);
        Assert.Empty(result.Recoveries);
        // No disputes, so the adjudicator never runs.
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task VerifyAsync_critic_rejection_reaches_the_adjudicator()
    {
        const string criticRejectsDog = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "A Dog is an Animal", "reason": "a specific dog"},
                {"label": "Collar", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Collar is worn by a Dog"}
              ],
              "subclass_decisions": [
                {"sub": "Dog", "super": "Animal", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        const string adjudicatorRestoresDog = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.9,
                 "evidence": "A Dog is an Animal", "reason": "category in context"}
              ]
            }
            """;
        var chat = new FakeChat()
            .Enqueue(criticRejectsDog)
            .Enqueue(adjudicatorRestoresDog)
            .Enqueue(FakeChat.VerifyDenotationAcceptAll);

        var result = await Service.VerifyAsync(chat, Text, Delta, CancellationToken.None);

        // The adjudicator's recovery is re-attached after the denotation
        // pass; the critic's rejection of Dog is superseded by the
        // adjudicator's acceptance, so nothing is reported as rejected.
        Assert.Equal(3, result.Delta.Classes.Count);
        Assert.Contains(result.Recoveries, r => r.Label == "Dog");
        Assert.Empty(result.Rejections);
        Assert.Equal(3, chat.CallCount);
    }

    [Fact]
    public async Task VerifyAsync_adjudicator_failure_is_fail_soft()
    {
        // Critic rejects everything; the adjudicator call throws. Python
        // logs and proceeds to the denotation pass over the original
        // candidates, which can only keep already-eligible norms (none), so
        // the chunk ends up empty rather than failed.
        const string criticRejectsAll = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"},
                {"label": "Collar", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "A Collar is worn by a Dog"}
              ]
            }
            """;
        var chat = new FailOnCallChat(2)
            .Enqueue(criticRejectsAll)
            .Enqueue(FakeChat.VerifyDenotationAcceptAll);

        var result = await Service.VerifyAsync(chat, Text, Delta, CancellationToken.None);

        Assert.Empty(result.Delta.Classes);
        Assert.Equal(3, chat.CallCount);
    }

    [Fact]
    public async Task VerifyAsync_denotation_rejection_strips_references()
    {
        // Denotation rejects Collar; its disjoint_with(Dog,Collar) axiom and
        // any property referencing it must disappear with the class.
        const string denotationRejectsCollar = """
            {
              "class_decisions": [
                {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"},
                {"label": "Collar", "role": "type", "keep": false, "confidence": 0.95,
                 "evidence": "A Collar is worn by a Dog", "reason": "an accessory, not a class"}
              ],
              "replacement_classes": []
            }
            """;
        var chat = new FakeChat()
            .Enqueue(FakeChat.VerifyCriticAcceptAll)
            .Enqueue(denotationRejectsCollar);

        var result = await Service.VerifyAsync(chat, Text, Delta, CancellationToken.None);

        Assert.Equal(new[] { "Animal", "Dog" },
            result.Delta.Classes.Select(c => c.Label));
        Assert.Contains(result.Rejections, r => r.Label == "Collar");
        Assert.DoesNotContain(result.Delta.Axioms, a => a.Type == "disjoint");
    }

    [Fact]
    public async Task VerifyAsync_denotation_recovers_a_suffix_replacement()
    {
        // The critic keeps "Sir Dog" as a type; the denotation critic has
        // the last word, demotes it to an individual and proposes the bare
        // "Dog" suffix as the real class.
        const string text = "Sir Dog is a titled animal. A Dog is an Animal.";
        var delta = new TBoxDelta(
            new[] { new ClassMutation("Sir Dog", "a titled dog") },
            Array.Empty<PropertyMutation>(), Array.Empty<PropertyMutation>(),
            Array.Empty<AxiomMutation>());
        const string criticKeeps = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "type", "keep": true, "confidence": 0.9,
                 "evidence": "Sir Dog is a titled animal"}
              ]
            }
            """;
        const string denotationReplaces = """
            {
              "class_decisions": [
                {"label": "Sir Dog", "role": "individual", "keep": false, "confidence": 0.95,
                 "evidence": "Sir Dog is a titled animal"}
              ],
              "replacement_classes": [
                {"from": "Sir Dog", "label": "Dog", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var chat = new FakeChat()
            .Enqueue(criticKeeps)
            .Enqueue(denotationReplaces);

        var result = await Service.VerifyAsync(chat, text, delta, CancellationToken.None);

        var recovered = Assert.Single(result.Delta.Classes);
        Assert.Equal("Dog", recovered.Label);
        Assert.Contains(result.Recoveries, r => r.Label == "Dog");
        Assert.Contains(result.Rejections, r => r.Label == "Sir Dog");
        // No disputes after the critic, so the adjudicator never runs.
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public async Task VerifyAsync_malformed_critic_reply_rejects_everything()
    {
        // "{}" (the FakeChat fallback) carries no decisions, so the
        // fail-closed boundary rejects every candidate. The adjudicator is
        // given the same empty reply and the denotation pass has no eligible
        // candidates left to check — nothing survives.
        var chat = new FakeChat().Enqueue("{}");

        var result = await Service.VerifyAsync(chat, Text, Delta, CancellationToken.None);

        Assert.Empty(result.Delta.Classes);
        Assert.Equal(3, result.Rejections.Count);
        Assert.Equal(2, chat.CallCount);
    }

    // ------------------------------------------------------------------
    // Orchestrator wiring (end-to-end against real collaborators)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Orchestrator_runs_verify_between_extract_and_merge()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontopilot-verify-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        try
        {
            using var store = new StoreWrapper(Path.Combine(root, "store"));
            using var contexts = new SqliteContextFactory();
            var ksId = Guid.NewGuid();
            const string graphIri = "http://goodcrew.local/ks/verify-tests";
            const string baseIri = graphIri + "/onto#";
            using (var db = contexts.CreateDbContext())
            {
                db.KnowledgeSystems.Add(new KnowledgeSystemEntity
                {
                    Id = ksId,
                    LegacyId = TestLegacyIds.Next("knowledgesystem"),
                    PublicId = Guid.NewGuid().ToString("N"),
                    Name = "Verify fixture",
                    GraphIri = graphIri,
                    BaseIri = baseIri,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                db.SaveChanges();
            }

            // Seed the Person class the delta's property domains reference, so
            // the graph ends up with exactly the three verified classes plus
            // Person (SchemaBuilder.EnsureClass mints it on demand otherwise).
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
                    .EnqueueValidDelta()
                    .EnqueueVerifyAcceptAll();
                FakeChatClientFactory.Default.Reset();
                FakeChatClientFactory.Default.UseClient(chat);

                var jobs = new ExtractionJobStore(contexts, TimeProvider.System);
                var orchestrator = new ExtractionOrchestrator(
                    jobs,
                    blobs,
                    new DocumentParser(),
                    new Chunker(size: 400, overlap: 20),
                    FakeChatClientFactory.Default,
                    new EndpointCapacityCoordinator(),
                    new TBoxExtractionService(Options.Create(new OnToPilotOptions())),
                    new ABoxExtractionService(Options.Create(new OnToPilotOptions())),
                    new TerminologyService(store),
                    new PromptSnapshotService(),
                    new FakeMerger(new ExtractionMerger(store)),
                    store,
                    TimeProvider.System,
                    verify: new TBoxVerifyService(Options.Create(new OnToPilotOptions())));

                var request = new ExtractionRequest(
                    KnowledgeSystemId: ksId,
                    BlobSha: sha,
                    FileName: "verify-fixture.txt",
                    Provider: "openai",
                    Model: "fake-model",
                    Endpoint: "https://fake.test/v1",
                    ApiKey: null,
                    ConcurrencyLimit: 2);
                var job = await orchestrator.StartTBoxAsync(request, CancellationToken.None);
                var finished = await jobs.WaitAsync(job.Id);

                Assert.Equal("completed", finished.Status);
                // extract + critic + denotation — no adjudicator call.
                Assert.Equal(3, chat.CallCount);

                var ks = new KsContext(graphIri, baseIri, "Verify fixture");
                var classCount = store.Match(
                    predicateIri: Vocabulary.RdfType.Value,
                    objectIri: Vocabulary.OwlClass.Value,
                    graphIri: ks.TBoxGraph).Count;
                // Seeded Person + the three verified classes.
                Assert.Equal(4, classCount);

                // The prompt snapshot records the three verify prompts.
                var prompts = finished.PromptSnapshot!.RootElement.GetProperty("prompts");
                foreach (var key in new[]
                {
                    TBoxVerifyService.BoundaryCriticKey,
                    TBoxVerifyService.BoundaryAdjudicatorKey,
                    TBoxVerifyService.DenotationCriticKey,
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

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
    }

    /// <summary>
    /// Chat client that returns the queued reply for every call except the
    /// Nth, which throws — used to prove the adjudicator's fail-soft path.
    /// </summary>
    private sealed class FailOnCallChat : IChatClient
    {
        private readonly int _failOnCall;
        private readonly Queue<string> _replies = new();
        private int _calls;

        public FailOnCallChat(int failOnCall) => _failOnCall = failOnCall;

        public int CallCount => Volatile.Read(ref _calls);

        public FailOnCallChat Enqueue(string reply)
        {
            _replies.Enqueue(reply);
            return this;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == _failOnCall)
            {
                throw new InvalidOperationException("simulated provider failure");
            }
            var reply = _replies.Count > 0 ? _replies.Dequeue() : "{}";
            return await Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))).ConfigureAwait(false);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
