using System.Text.Json;
using ISEStudio.Extraction;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Decision-helper tests for the job-level corpus recovery pass (Python
/// <c>_recover_rejected_classes</c>). The fail-closed boundary
/// (<see cref="CorpusRecoveryService.ApplyCorpusRoleDecisions"/>) is the
/// load-bearing contract: every accepted label must clear five independent
/// checks, and the <see cref="CorpusRecoveryService.BuildCandidates"/>
/// upstream of it must drop labels that already exist in the graph or are
/// XSD datatype aliases. LLM-dependent halves (selector + recovery prompts)
/// are not exercised here — they are deterministic only when paired with a
/// canned reply, and the pipeline test in
/// <c>TBoxVerifyServiceTests.Orchestrator_runs_verify_between_extract_and_merge</c>
/// covers the wire-up.
/// </summary>
public sealed class CorpusRecoveryServiceTests
{
    private const string Text = FakeChat.VerifySourceText;

    // ------------------------------------------------------------------
    // BuildCandidates
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCandidates_skips_labels_already_in_the_graph()
    {
        var chunk = new CorpusRecoveryChunk(
            ChunkId: 0,
            Text: Text,
            Rejected: new[]
            {
                new RejectedClass("Animal", "individual", Evidence: "The Animal kingdom has many species"),
            });
        var existing = new HashSet<string>(StringComparer.Ordinal) { TBoxVerifyService.LabelNorm("Animal") };

        var candidates = CorpusRecoveryService.BuildCandidates(
            new[] { chunk }, existing);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildCandidates_skips_xsd_datatype_aliases()
    {
        // "decimal" is an XSD canonical datatype (Vocabulary.CanonicalDatatypeName).
        var chunk = new CorpusRecoveryChunk(
            ChunkId: 0,
            Text: "A value of type decimal is measured in grams",
            Rejected: new[]
            {
                new RejectedClass("decimal", "datatype alias", Evidence: "type decimal is measured"),
            });

        var candidates = CorpusRecoveryService.BuildCandidates(
            new[] { chunk }, new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildCandidates_skips_ungrounded_labels()
    {
        var chunk = new CorpusRecoveryChunk(
            ChunkId: 0,
            Text: Text,
            Rejected: new[]
            {
                // "Phantom" is the rejected label, but the source text never
                // mentions it — the label is not surface-grounded.
                new RejectedClass("Phantom", "made up", Evidence: "The Animal kingdom has many species"),
            });

        var candidates = CorpusRecoveryService.BuildCandidates(
            new[] { chunk }, new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildCandidates_groups_occurrences_across_chunks()
    {
        // Two chunks reject the same label; the candidate carries both
        // occurrences (one per chunk) so the corpus recovery sees the
        // union rather than just one chunk's view.
        var chunks = new[]
        {
            new CorpusRecoveryChunk(ChunkId: 0, Text: Text,
                Rejected: new[] { new RejectedClass("Dog", "individual", Evidence: "A Dog is an Animal") }),
            new CorpusRecoveryChunk(ChunkId: 1, Text: Text,
                Rejected: new[] { new RejectedClass("Dog", "individual", Evidence: "A Dog is an Animal") }),
        };

        var candidates = CorpusRecoveryService.BuildCandidates(
            chunks, new HashSet<string>(StringComparer.Ordinal));

        var dog = Assert.Single(candidates);
        Assert.Equal("Dog", dog.Value.Label);
        Assert.Equal(2, dog.Value.Occurrences.Count);
    }

    [Fact]
    public void BuildCandidates_deduplicates_occurrences_within_a_chunk()
    {
        // Same chunk twice in the rejection list must collapse — only one
        // occurrence survives per chunk, mirroring Python's `if norm in seen`.
        var chunks = new[]
        {
            new CorpusRecoveryChunk(ChunkId: 0, Text: Text,
                Rejected: new[]
                {
                    new RejectedClass("Dog", "individual", Evidence: "A Dog is an Animal"),
                    new RejectedClass("Dog", "individual", Evidence: "A Dog is an Animal"),
                }),
        };

        var candidates = CorpusRecoveryService.BuildCandidates(
            chunks, new HashSet<string>(StringComparer.Ordinal));

        var dog = Assert.Single(candidates);
        Assert.Single(dog.Value.Occurrences);
    }

    // ------------------------------------------------------------------
    // ApplyCorpusEvidenceSelections
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyCorpusEvidenceSelections_trusts_numbered_passage_ids()
    {
        // Build the prepared-passage map directly to focus the test on the
        // selection logic itself: the helper must honour the model's
        // passage_id picks and cap at the requested limit, regardless of
        // how PrepareCorpusEvidence produces its windows in production.
        var label = TBoxVerifyService.LabelNorm("Dog");
        var prepared = new Dictionary<string, PreparedCorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new PreparedCorpusCandidate("Dog", new List<PreparedPassage>
            {
                new("p1", 0, "First passage text", "individual", "evidence 1"),
                new("p2", 0, "Second passage text", "individual", "evidence 2"),
                new("p3", 0, "Third passage text", "individual", "evidence 3"),
            }),
        };

        // The model picks passage p2 only — p1 and p3 are deliberately
        // excluded. The helper must honour the selection.
        const string payload = """
            {
              "evidence_selections": [
                {"label": "Dog", "passage_ids": ["p2"]}
              ]
            }
            """;
        var selected = CorpusRecoveryService.ApplyCorpusEvidenceSelections(
            prepared, Payload(payload), limit: 4);

        var chosen = selected.Single().Value;
        var passage = Assert.Single(chosen);
        Assert.Equal("p2", passage.PassageId);
        Assert.Equal("Second passage text", passage.Text);
    }

    [Fact]
    public void ApplyCorpusEvidenceSelections_caps_at_the_limit()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var prepared = CorpusRecoveryService.PrepareCorpusEvidence(
            new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
            {
                [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
                {
                    new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                        EarlierReason: "individual"),
                }),
            });
        // PrepareCorpusEvidence emits up to 2 windows for one occurrence
        // (label_evidence_windows limit=2), so asking for 5 must fall back
        // to the diverse sample when the helper trusts only what's listed.
        const string payload = """
            {
              "evidence_selections": [
                {"label": "Dog", "passage_ids": ["p1", "p2", "p3", "p4", "p5"]}
              ]
            }
            """;
        var selected = CorpusRecoveryService.ApplyCorpusEvidenceSelections(
            prepared, Payload(payload), limit: 4);

        var chosen = selected.Single().Value;
        Assert.True(chosen.Count <= 4, $"expected <= 4 selected, got {chosen.Count}");
    }

    [Fact]
    public void ApplyCorpusEvidenceSelections_falls_back_when_payload_is_empty()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var prepared = CorpusRecoveryService.PrepareCorpusEvidence(
            new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
            {
                [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
                {
                    new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                        EarlierReason: "individual"),
                }),
            });

        // Empty payload — selector failed or produced garbage. The fallback
        // must still produce a usable selection (diverse passages).
        var selected = CorpusRecoveryService.ApplyCorpusEvidenceSelections(
            prepared, Payload("{}"), limit: 4);

        var chosen = selected.Single().Value;
        Assert.NotEmpty(chosen);
    }

    [Fact]
    public void ApplyCorpusEvidenceSelections_ignores_unknown_passage_ids()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var prepared = CorpusRecoveryService.PrepareCorpusEvidence(
            new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
            {
                [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
                {
                    new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                        EarlierReason: "individual"),
                }),
            });

        // All passage_ids are unknown → fallback to diverse passages.
        const string payload = """
            {
              "evidence_selections": [
                {"label": "Dog", "passage_ids": ["p9", "pZ"]}
              ]
            }
            """;
        var selected = CorpusRecoveryService.ApplyCorpusEvidenceSelections(
            prepared, Payload(payload), limit: 4);

        var chosen = selected.Single().Value;
        Assert.NotEmpty(chosen);
        Assert.DoesNotContain(chosen, p => p.PassageId == "p9");
    }

    // ------------------------------------------------------------------
    // ApplyCorpusRoleDecisions — fail-closed boundary
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyCorpusRoleDecisions_accepts_grounded_type_decision()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        var row = Assert.Single(accepted);
        Assert.Equal("Dog", row.Label);
    }

    [Fact]
    public void ApplyCorpusRoleDecisions_rejects_string_true_keep()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        // "true" as a string — Python `keep is True` (identity check) does
        // not accept it; the .NET helper mirrors via ValueKind != True.
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": "true", "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplyCorpusRoleDecisions_rejects_confidence_below_floor()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.5,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplyCorpusRoleDecisions_rejects_individual_role()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        // role="individual" — the boundary helper requires role=="type".
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "individual", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplyCorpusRoleDecisions_rejects_explicit_individual_declaration()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        // The chunk carries an explicit `Dog is an instance of ...` line —
        // even a keep=true type decision cannot override that declaration.
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0,
                    Text: "There exists a Dog. Dog is an instance of Canine. A Dog is an Animal.",
                    ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplyCorpusRoleDecisions_rejects_ungrounded_evidence()
    {
        var label = TBoxVerifyService.LabelNorm("Dog");
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal)
        {
            [label] = new CorpusCandidate("Dog", new List<CorpusOccurrence>
            {
                new(ChunkId: 0, Text: Text, ExtractorEvidence: "A Dog is an Animal",
                    EarlierReason: "individual"),
            }),
        };
        // Evidence quote never appears in the source text.
        const string payload = """
            {
              "class_decisions": [
                {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95,
                 "evidence": "a hallucinated paragraph that never appears in the source"}
              ]
            }
            """;

        var accepted = CorpusRecoveryService.ApplyCorpusRoleDecisions(
            candidates, Payload(payload),
            structuredNonTypeSignals: new HashSet<string>(StringComparer.Ordinal),
            floor: 0.85);

        Assert.Empty(accepted);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}