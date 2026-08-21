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
}