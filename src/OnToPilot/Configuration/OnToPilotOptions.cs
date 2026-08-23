namespace OnToPilot.Configuration;

/// <summary>
/// Top-level configuration for OnToPilot. Bound from the "OnToPilot" section of
/// configuration sources (appsettings.json, environment variables, etc.).
/// </summary>
public sealed class OnToPilotOptions
{
    public const string SectionName = "OnToPilot";

    /// <summary>
    /// BCP-47-ish language tag used for prompts and UI strings. Mirrors the
    /// Python backend's <c>system_language</c> setting.
    /// </summary>
    public string SystemLanguage { get; set; } = "en";

    /// <summary>
    /// Default LLM model used for extraction. Mirrors the Python backend's
    /// <c>llm_extract_model</c> setting.
    /// </summary>
    public string ExtractModel { get; set; } = "deepseek/deepseek-chat";

    /// <summary>
    /// API key for the LLM provider. Whether this is set (non-empty) is
    /// surfaced via the health endpoint as <c>has_llm_key</c>. The key value
    /// itself is never exposed.
    /// </summary>
    public string? LlmApiKey { get; set; }

    /// <summary>
    /// Name of the opaque session cookie. Matches the Python backend's
    /// <c>session_cookie</c> setting so existing client tooling continues
    /// to work during the .NET migration.
    /// </summary>
    public string SessionCookie { get; set; } = "ontopilot_session";

    /// <summary>
    /// Server-side session lifetime in hours. Matches the Python backend's
    /// <c>session_ttl_hours</c> (default 2 weeks).
    /// </summary>
    public int SessionTtlHours { get; set; } = 24 * 14;

    /// <summary>
    /// Whether the session cookie is marked <c>Secure</c>. Mirrors the
    /// Python backend's <c>cookie_secure</c>; default false so local HTTP
    /// development works out of the box.
    /// </summary>
    public bool CookieSecure { get; set; }

    /// <summary>Maximum RDF import upload size in bytes. Mirrors Python <c>rdf_import_max_bytes</c>.</summary>
    public int RdfImportMaxBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Maximum parsed RDF statements accepted by a single import.</summary>
    public int RdfImportMaxTriples { get; set; } = 250_000;

    /// <summary>Whether TBox RDF imports trigger controlled terminology synchronization.</summary>
    public bool AutomaticTerminology { get; set; } = true;

    /// <summary>
    /// Maximum number of parsed chunks fed to a single terminology-agent
    /// suggest pass. Mirrors the Python backend's
    /// <c>terminology_suggestion_max_chunks</c> setting (default 50).
    /// </summary>
    public int TerminologySuggestionMaxChunks { get; set; } = 50;

    /// <summary>
    /// Whether the extraction-job pipeline runs the LLM-driven terminology
    /// proposal pass after the deterministic sync. Mirrors the Python
    /// backend's <c>terminology_suggest_during_extraction</c> setting.
    /// </summary>
    public bool TerminologySuggestDuringExtraction { get; set; } = true;

    /// <summary>
    /// Whether the conflict agent triages open <c>duplicate</c> /
    /// <c>predicate_specialization</c> conflicts after detection and attaches
    /// a recommendation for human confirmation. Mirrors the Python backend's
    /// <c>agentic_conflict_resolution</c> setting (default true).
    /// </summary>
    public bool AgenticConflictResolution { get; set; } = true;

    /// <summary>
    /// ReAct tool-call budget per conflict for the conflict agent. Mirrors the
    /// Python backend's <c>conflict_agent_max_steps</c> setting.
    /// </summary>
    public int ConflictAgentMaxSteps { get; set; } = 3;

    /// <summary>
    /// Whether the structure agent attaches isolated classes (no parent, no
    /// children, not a property domain/range) under a broader parent after
    /// extraction / conflict detection. Mirrors the Python backend's
    /// <c>agentic_isolated_classes</c> setting (default true).
    /// </summary>
    public bool AgenticIsolatedClasses { get; set; } = true;

    /// <summary>
    /// Confidence floor for auto-applying an agent suggestion without human
    /// confirmation. Mirrors the Python backend's
    /// <c>conflict_auto_apply_floor</c> setting (0.85) — shared by the
    /// structure agent (auto-attaches a parent at or above this confidence)
    /// and the conflict agent (auto-applies the chosen resolution at or
    /// above this confidence; below-floor decisions attach a recommendation
    /// for human confirmation instead).
    /// </summary>
    public double AutoApplyFloor { get; set; } = 0.85;

    /// <summary>
    /// A parent proposed for more than this many isolated classes is
    /// treated as an over-general catch-all and left for a human. Mirrors
    /// the Python backend's <c>structure_max_same_parent</c> setting.
    /// </summary>
    public int StructureMaxSameParent { get; set; } = 5;

    /// <summary>
    /// Root prefix used when stamping a fresh knowledge system's
    /// <see cref="Infrastructure.Persistence.Entities.KnowledgeSystemEntity.GraphIri"/>
    /// / <see cref="Infrastructure.Persistence.Entities.KnowledgeSystemEntity.BaseIri"/>.
    /// Mirrors the Python backend's <c>GRAPH_ROOT</c> setting so .NET and
    /// Python stamp byte-identical IRIs for the same KS id. Configurable
    /// so a future IRI migration is a config change rather than a code
    /// change.
    /// </summary>
    public string IriRoot { get; set; } = "http://goodcrew.local/ks";

    /// <summary>
    /// Prefix used by the OnToPilot vocabulary namespace (the <c>op:</c>
    /// shorthand in Turtle). Must end with <c>#</c> — the runtime
    /// concatenates predicate local names (e.g. <c>defaultLanguage</c>,
    /// <c>status</c>, <c>mapsTo</c>, <c>origin</c>) onto this prefix to
    /// form full predicate IRIs, and the SHACL shapes loader
    /// string-replaces its hard-coded <c>op:</c> prefix with this value
    /// at load time. Mirrors the Python backend's
    /// <c>ONTOPILOT</c> / <c>settings.vocab_namespace</c>.
    /// </summary>
    public string VocabNamespace { get; set; } = "http://goodcrew.local/vocab#";
}