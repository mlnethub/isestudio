using OnToPilot.Ontology;

namespace OnToPilot.Tests.Configuration;

/// <summary>
/// Phase 3 IRI prefix wire-up tests. Covers the runtime side of the
/// <c>http://ontopilot.local/</c> → <c>http://goodcrew.local/</c>
/// migration introduced in Phase 0/1:
/// <list type="bullet">
///   <item><see cref="SkosVocab.Ontopilot"/> reads from
///         <c>OnToPilot:VocabNamespace</c> after <see cref="SkosVocab.Configure"/>.</item>
///   <item>Derived <c>Op*</c> NamedNodes track the configured prefix.</item>
///   <item><see cref="SkosVocab.Configure"/> rejects a missing <c>#</c>
///         suffix because the SHACL loader concatenates
///         <c>Ontopilot + "predicate"</c>.</item>
/// </list>
/// <see cref="OnToPilotOptionsTests"/> covers the option-default side;
/// this file covers the <see cref="SkosVocab"/> static state-machine side.
/// See <c>migration/runbooks/iri-migration-runbook.md</c> for the
/// cutover procedure that depends on these invariants.
/// </summary>
public sealed class IriPrefixDefaultTests : IDisposable
{
    private readonly string _originalOntopilot;

    public IriPrefixDefaultTests()
    {
        _originalOntopilot = SkosVocab.Ontopilot;
    }

    public void Dispose()
    {
        // Reset every test back to the configured default so test order
        // does not matter. Configure(null) would throw, so we restore
        // the value captured at construction time.
        SkosVocab.Configure(_originalOntopilot);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Ontopilot_defaults_to_goodcrew_local_vocab_namespace()
    {
        // The static field initializer (SkosManager.cs:28) sets the
        // default; this test pins it so a future accidental revert to
        // the legacy prefix is caught at unit-test time.
        Assert.Equal("http://goodcrew.local/vocab#", SkosVocab.Ontopilot);
        Assert.DoesNotContain("ontopilot.local", SkosVocab.Ontopilot);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_updates_Ontopilot_and_derived_Op_predicates()
    {
        const string newPrefix = "http://parity-test.local/vocab#";
        SkosVocab.Configure(newPrefix);

        Assert.Equal(newPrefix, SkosVocab.Ontopilot);
        // The lazy Op* NamedNodes must rebuild on Configure so
        // TerminologyService and SkosManager see the new prefix.
        Assert.Equal(newPrefix + "defaultLanguage", SkosVocab.OpDefaultLanguage.Value);
        Assert.Equal(newPrefix + "status", SkosVocab.OpStatus.Value);
        Assert.Equal(newPrefix + "mapsTo", SkosVocab.OpMapsTo.Value);
        Assert.Equal(newPrefix + "origin", SkosVocab.OpOrigin.Value);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_rejects_namespace_without_trailing_hash()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SkosVocab.Configure("http://parity-test.local/vocab"));
        Assert.Contains("must end with '#'", ex.Message);

        // Rejection must not mutate the live Ontopilot prefix.
        Assert.Equal(_originalOntopilot, SkosVocab.Ontopilot);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_rejects_null_or_empty_namespace()
    {
        Assert.ThrowsAny<ArgumentException>(() => SkosVocab.Configure(null!));
        Assert.ThrowsAny<ArgumentException>(() => SkosVocab.Configure(""));

        // Ontopilot must still match the captured original value.
        Assert.Equal(_originalOntopilot, SkosVocab.Ontopilot);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_to_legacy_prefix_then_back_round_trips()
    {
        // Simulates a cutover rollback scenario where the operator
        // restores the legacy prefix in code (the data layer is still
        // on the new prefix; rollback is code-only). The Configure call
        // itself must accept any well-formed namespace; the
        // forward/back transition must be lossless.
        const string legacy = "http://ontopilot.local/vocab#";
        const string modern = "http://goodcrew.local/vocab#";

        SkosVocab.Configure(legacy);
        Assert.Equal(legacy, SkosVocab.Ontopilot);
        Assert.Equal(legacy + "status", SkosVocab.OpStatus.Value);

        SkosVocab.Configure(modern);
        Assert.Equal(modern, SkosVocab.Ontopilot);
        Assert.Equal(modern + "status", SkosVocab.OpStatus.Value);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_derived_Op_predicates_start_with_configured_prefix()
    {
        // Independent of any Configure call: the Op* NamedNode values
        // must always be Ontopilot + localName so SHACL shape
        // round-trips stay byte-identical across configuration
        // transitions.
        foreach (var (node, local) in new[]
        {
            (SkosVocab.OpDefaultLanguage, "defaultLanguage"),
            (SkosVocab.OpStatus, "status"),
            (SkosVocab.OpMapsTo, "mapsTo"),
            (SkosVocab.OpOrigin, "origin"),
        })
        {
            Assert.Equal(SkosVocab.Ontopilot + local, node.Value);
        }
    }
}
