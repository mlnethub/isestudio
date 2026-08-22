namespace OnToPilot.Prompts;

/// <summary>
/// Static catalog of registered prompt templates. MVP seed covering all four
/// categories the frontend's <c>PromptCategory</c> union recognises
/// ("extraction" / "review" / "governance" / "validation"). The final 1:1
/// mapping with the Python <c>prompt_config</c> registry is pending
/// inventory — keys + default_content here are reasonable placeholders so
/// the wire surface is observable end-to-end; semantic alignment is
/// expected to land in a follow-up slice.
/// </summary>
public static class PromptCatalog
{
    private static readonly IReadOnlyList<PromptDef> _entries = new PromptDef[]
    {
        // extraction
        new(
            "extraction.system",
            "extraction",
            "Extraction system prompt",
            "Guides the LLM when extracting RDF triples from source documents.",
            "You are a knowledge engineer. Extract RDF triples (subject predicate object) from the supplied text. Use Turtle syntax. Preserve original literals exactly.",
            Array.Empty<string>()),
        new(
            "extraction.user",
            "extraction",
            "Extraction user prompt",
            "Wraps the source text the LLM should mine for triples.",
            "Source text:\n---\n{{source_text}}\n---\nExtract every relevant RDF triple.",
            new[] { "source_text" }),

        // review
        new(
            "review.system",
            "review",
            "Review system prompt",
            "Guides the LLM when reviewing extracted triples for correctness.",
            "You are an ontology reviewer. Identify malformed, redundant, or low-quality triples.",
            Array.Empty<string>()),

        // governance
        new(
            "governance.system",
            "governance",
            "Governance system prompt",
            "Guides the LLM when proposing governance decisions over conflicts.",
            "You are a governance assistant. Recommend resolutions for the listed conflicts, citing the source assertions.",
            Array.Empty<string>()),

        // validation
        new(
            "validation.system",
            "validation",
            "Validation system prompt",
            "Guides the LLM when validating a knowledge system against its shapes.",
            "You are a SHACL validator. Summarize the violations and group them by severity.",
            Array.Empty<string>()),
    };

    public static IReadOnlyList<PromptDef> All => _entries;

    public static PromptDef? Find(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));
}