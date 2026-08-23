using OnToPilot.Extraction;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// <see cref="ExtractionDeltaParser"/> tests for the extractor-evidence
/// propagation slice (P1-5a ADR §5 follow-up). The Python pipeline
/// (<c>extract.py:83,86</c>) carries an <c>evidence</c> source span on every
/// class candidate and every subclass axiom; .NET previously dropped the
/// field, so the verify critic's <c>extractor_evidence</c> was always
/// empty. These tests pin the parser's new behaviour: presence is forwarded,
/// absence leaves the field null, and the prior shape (no evidence at all)
/// still parses cleanly.
/// </summary>
public sealed class ExtractionDeltaParserTests
{
    // ------------------------------------------------------------------
    // Class evidence
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTBox_forwards_class_evidence()
    {
        const string reply = """
            {
              "classes": [
                {"label": "Animal", "comment": "A living creature",
                 "evidence": "The Animal kingdom has many species"},
                {"label": "Dog", "comment": "A domesticated canid",
                 "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        Assert.Equal(2, delta.Classes.Count);
        Assert.Equal("The Animal kingdom has many species", delta.Classes[0].Evidence);
        Assert.Equal("A Dog is an Animal", delta.Classes[1].Evidence);
    }

    [Fact]
    public void ParseTBox_class_without_evidence_yields_null()
    {
        // A reply that predates the field addition must still parse
        // cleanly; downstream code (TBoxVerifyService) handles null with
        // `c.Evidence ?? ""`.
        const string reply = """
            {
              "classes": [
                {"label": "Animal", "comment": "A living creature"}
              ]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        var animal = Assert.Single(delta.Classes);
        Assert.Null(animal.Evidence);
    }

    [Fact]
    public void ParseTBox_class_with_empty_evidence_string_yields_null()
    {
        // The Python equivalent uses `str(row.get("evidence") or "")` — a
        // missing key, an explicit null, and an empty string all collapse
        // to "" / null in .NET so the critic payload carries an empty
        // extractor_evidence span.
        const string reply = """
            {
              "classes": [
                {"label": "Animal", "comment": "x", "evidence": ""}
              ]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        var animal = Assert.Single(delta.Classes);
        Assert.Null(animal.Evidence);
    }

    // ------------------------------------------------------------------
    // Subclass axiom evidence
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTBox_forwards_subclass_evidence()
    {
        const string reply = """
            {
              "subclass_of": [
                {"sub": "Dog", "super": "Animal", "evidence": "A Dog is an Animal"}
              ]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        var edge = Assert.Single(delta.Axioms);
        Assert.Equal("subclass", edge.Type);
        Assert.Equal("Dog", edge.Sub);
        Assert.Equal("Animal", edge.Super);
        Assert.Equal("A Dog is an Animal", edge.Evidence);
    }

    [Fact]
    public void ParseTBox_subclass_without_evidence_yields_null()
    {
        // A subclass reply without evidence (older extractor prompts, or
        // a model that forgot the field) must still parse; downstream
        // emits the critic payload with an empty extractor_evidence span.
        const string reply = """
            {
              "subclass_of": [
                {"sub": "Dog", "super": "Animal"}
              ]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        var edge = Assert.Single(delta.Axioms);
        Assert.Null(edge.Evidence);
    }

    // ------------------------------------------------------------------
    // Disjoint / equivalent axioms never carry evidence
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTBox_disjoint_with_axiom_evidence_is_ignored()
    {
        // Python's disjoint / equivalent payloads don't carry an evidence
        // field, and even if a model sneaks one in, the parser ignores it
        // — the merger never reads AxiomMutation.Evidence for non-subclass
        // types.
        const string reply = """
            {
              "disjoint_with": [{"a": "Dog", "b": "Collar", "evidence": "ignored"}]
            }
            """;
        var delta = ExtractionDeltaParser.ParseTBox(reply);

        var edge = Assert.Single(delta.Axioms);
        Assert.Equal("disjoint", edge.Type);
        Assert.Null(edge.Evidence);
    }

    // ------------------------------------------------------------------
    // Backward compat: replies that pre-date the field
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTBox_legacy_payload_still_parses_cleanly()
    {
        // The full ValidTBoxDelta used by every extraction test predates
        // the evidence field. This is a regression guard so the slice
        // doesn't quietly break the extraction test suite.
        var delta = ExtractionDeltaParser.ParseTBox(FakeChat.ValidTBoxDelta);

        Assert.Equal(3, delta.Classes.Count);
        Assert.All(delta.Classes, c => Assert.Null(c.Evidence));
        Assert.All(delta.Axioms.Where(a => a.Type == "subclass"),
            a => Assert.Null(a.Evidence));
    }
}