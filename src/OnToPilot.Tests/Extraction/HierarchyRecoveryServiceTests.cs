using System.Text.Json;
using OnToPilot.Extraction;
using ProposedEdge = OnToPilot.Extraction.HierarchyRecoveryService.ProposedEdge;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// Decision-helper tests for the per-chunk hierarchy recovery pass (Python
/// <c>_recover_hierarchy_one</c> / <c>_verify_subclass_candidates</c>).
/// The fail-closed boundary
/// (<see cref="HierarchyRecoveryService.ApplySubclassDecisions"/>) requires
/// <c>keep is True</c> at or above the floor, grounded evidence, and both
/// endpoints inside the allowed-norms set. The LLM-driven halves
/// (recovery + critic) are not exercised here; the orchestrator integration
/// test in
/// <c>TBoxVerifyServiceTests.Orchestrator_runs_verify_between_extract_and_merge</c>
/// covers the wire-up.
/// </summary>
public sealed class HierarchyRecoveryServiceTests
{
    private const string Text = FakeChat.VerifySourceText;

    [Fact]
    public void ApplySubclassDecisions_accepts_grounded_edge_at_or_above_floor()
    {
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Animal", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Animal", "keep": true, "confidence": 0.9,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        var edge = Assert.Single(accepted);
        Assert.Equal("Dog", edge.Sub);
        Assert.Equal("Animal", edge.Super);
    }

    [Fact]
    public void ApplySubclassDecisions_rejects_string_true_keep()
    {
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Animal", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        // "true" as a string — Python `keep is True` rejects it.
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Animal", "keep": "true", "confidence": 0.9,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplySubclassDecisions_rejects_confidence_below_floor()
    {
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Animal", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Animal", "keep": true, "confidence": 0.5,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplySubclassDecisions_rejects_ungrounded_evidence()
    {
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Animal", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Animal", "keep": true, "confidence": 0.9,
                 "evidence": "this never appears in the source"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplySubclassDecisions_drops_endpoints_outside_allowed_norms()
    {
        // Critic approved the edge, but the super endpoint ("Thing") is
        // not in the merged class vocabulary — the helper trusts nothing
        // outside the universe. Mirrors Python's allowed_norms check.
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Thing", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Thing", "keep": true, "confidence": 0.95,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        Assert.Empty(accepted);
    }

    [Fact]
    public void ApplySubclassDecisions_accepts_subclass_alias_field_names()
    {
        // Some models emit `child` / `parent` rather than `sub` / `super`.
        // The helper must still find the decision via the field-name aliases
        // (Python _subclass_pair fallback list).
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Animal", "A Dog is an Animal"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
            TBoxVerifyService.LabelNorm("Animal"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"child": "Dog", "parent": "Animal", "keep": true, "confidence": 0.9,
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

        var edge = Assert.Single(accepted);
        Assert.Equal("Dog", edge.Sub);
        Assert.Equal("Animal", edge.Super);
    }

    [Fact]
    public void ApplySubclassDecisions_drops_self_loop_via_evidence_grounding()
    {
        // The static helper's contract: every check must pass before the
        // edge lands. For a self-loop, the evidence "A Dog is a Dog" is not
        // in the source (which says "A Dog is an Animal"), so the grounding
        // check rejects it. The helper does not carry an explicit self-loop
        // guard — the loop guard lives in RecoverAsync — but in practice the
        // grounding check alone rejects it. We assert that behaviour.
        var proposed = new[]
        {
            new ProposedEdge("Dog", "Dog", "A Dog is a Dog"),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            TBoxVerifyService.LabelNorm("Dog"),
        };
        const string payload = """
            {
              "subclass_decisions": [
                {"sub": "Dog", "super": "Dog", "keep": true, "confidence": 0.9,
                 "evidence": "A Dog is a Dog"}
              ]
            }
            """;

        var accepted = HierarchyRecoveryService.ApplySubclassDecisions(
            Text, proposed, Payload(payload), allowed);

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