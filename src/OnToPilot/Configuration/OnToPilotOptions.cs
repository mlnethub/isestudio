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
}