using ISEStudio.Ontology;

namespace ISEStudio.Tests.Configuration;

/// <summary>
/// Phase 3 IRI prefix wire-up tests. Covers the runtime side of the
/// historical legacy-IRI → <c>http://goodcrew.local/</c> migration
/// introduced in Phase 0/1:
/// <list type="bullet">
///   <item><see cref="SkosVocab.IseStudio"/> reads from
///         <c>ISEStudio:VocabNamespace</c> after <see cref="SkosVocab.Configure"/>.</item>
///   <item>Derived <c>Op*</c> NamedNodes track the configured prefix.</item>
///   <item><see cref="SkosVocab.Configure"/> rejects a missing <c>#</c>
///         suffix because the SHACL loader concatenates
///         <c>IseStudio + "predicate"</c>.</item>
/// </list>
/// <see cref="ISEStudioOptionsTests"/> covers the option-default side;
/// this file covers the <see cref="SkosVocab"/> static state-machine side.
/// See <c>migration/runbooks/iri-migration-runbook.md</c> for the
/// cutover procedure that depends on these invariants.
/// </summary>
public sealed class IriPrefixDefaultTests : IDisposable
{
    private readonly string _originalIseStudio;

    public IriPrefixDefaultTests()
    {
        _originalIseStudio = SkosVocab.IseStudio;
    }

    public void Dispose()
    {
        // Reset every test back to the configured default so test order
        // does not matter. Configure(null) would throw, so we restore
        // the value captured at construction time.
        SkosVocab.Configure(_originalIseStudio);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_IseStudio_defaults_to_goodcrew_local_vocab_namespace()
    {
        // The static field initializer (SkosManager.cs:28) sets the
        // default; this test pins it so a future accidental revert to
        // a legacy prefix is caught at unit-test time.
        Assert.Equal("http://goodcrew.local/vocab#", SkosVocab.IseStudio);
        Assert.StartsWith("http://goodcrew.local/", SkosVocab.IseStudio);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_updates_IseStudio_and_derived_Op_predicates()
    {
        const string newPrefix = "http://parity-test.local/vocab#";
        SkosVocab.Configure(newPrefix);

        Assert.Equal(newPrefix, SkosVocab.IseStudio);
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

        // Rejection must not mutate the live IseStudio prefix.
        Assert.Equal(_originalIseStudio, SkosVocab.IseStudio);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_rejects_null_or_empty_namespace()
    {
        Assert.ThrowsAny<ArgumentException>(() => SkosVocab.Configure(null!));
        Assert.ThrowsAny<ArgumentException>(() => SkosVocab.Configure(""));

        // IseStudio must still match the captured original value.
        Assert.Equal(_originalIseStudio, SkosVocab.IseStudio);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_Configure_to_alternate_prefix_then_back_round_trips()
    {
        // Simulates a cutover rollback scenario where the operator
        // restores an alternate prefix in code (the data layer is still
        // on the new prefix; rollback is code-only). The Configure call
        // itself must accept any well-formed namespace; the
        // forward/back transition must be lossless.
        const string alternate = "http://rollback.local/vocab#";
        const string modern = "http://goodcrew.local/vocab#";

        SkosVocab.Configure(alternate);
        Assert.Equal(alternate, SkosVocab.IseStudio);
        Assert.Equal(alternate + "status", SkosVocab.OpStatus.Value);

        SkosVocab.Configure(modern);
        Assert.Equal(modern, SkosVocab.IseStudio);
        Assert.Equal(modern + "status", SkosVocab.OpStatus.Value);
    }

    [Fact]
    [Trait("Category", "Iri")]
    public void SkosVocab_derived_Op_predicates_start_with_configured_prefix()
    {
        // Independent of any Configure call: the Op* NamedNode values
        // must always be IseStudio + localName so SHACL shape
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
            Assert.Equal(SkosVocab.IseStudio + local, node.Value);
        }
    }
}
