using OnToPilot.Configuration;

namespace OnToPilot.Tests.Configuration;

/// <summary>
/// Default-value tests for <see cref="OnToPilotOptions"/>. Pin the
/// <c>IriRoot</c> + <c>VocabNamespace</c> defaults so a future IRI
/// migration is a deliberate config change rather than a silent
/// accidental flip. See
/// <c>migration/runbooks/iri-migration-runbook.md</c>.
/// </summary>
public sealed class OnToPilotOptionsTests
{
    [Fact]
    [Trait("Category", "Configuration")]
    public void Defaults_iri_root_to_goodcrew_local()
    {
        var opts = new OnToPilotOptions();
        Assert.Equal("http://goodcrew.local/ks", opts.IriRoot);
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Defaults_vocab_namespace_to_goodcrew_local_with_trailing_hash()
    {
        var opts = new OnToPilotOptions();
        Assert.Equal("http://goodcrew.local/vocab#", opts.VocabNamespace);
        // Trailing '#' is required: SKOS NamedNode predicates are built
        // by string concatenation (Ontopilot + "defaultLanguage"), and
        // the SHACL shape loader string-replaces this prefix into the
        // shapes file at load time.
        Assert.EndsWith("#", opts.VocabNamespace);
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void Defaults_are_not_legacy_ontopilot_local()
    {
        var opts = new OnToPilotOptions();
        // Guard against accidentally reverting to the old prefix.
        Assert.DoesNotContain("ontopilot.local", opts.IriRoot);
        Assert.DoesNotContain("ontopilot.local", opts.VocabNamespace);
    }

    [Fact]
    [Trait("Category", "Configuration")]
    public void IriRoot_and_VocabNamespace_are_independently_settable()
    {
        var opts = new OnToPilotOptions
        {
            IriRoot = "http://parity-test.local/ks",
            VocabNamespace = "http://parity-test.local/vocab#",
        };
        Assert.Equal("http://parity-test.local/ks", opts.IriRoot);
        Assert.Equal("http://parity-test.local/vocab#", opts.VocabNamespace);
    }
}