using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Extraction;

/// <summary>
/// Captures the prompt contents actually used by a run. Mirrors the Python
/// <c>prompt_config.snapshot()</c> shape
/// <c>{prompts: {&lt;prompt_key&gt;: {content, sha256, overridden}}}</c> so the
/// stored blob round-trips with the existing review tooling.
/// </summary>
/// <remarks>
/// <para>Prompt overrides are not part of this task's surface — every entry
/// reports <c>overridden=false</c>. The shape is reserved so a follow-up
/// task can wire <see cref="Infrastructure.Persistence.Entities.KnowledgePromptOverrideEntity"/>
/// rows into the snapshot without a migration.</para>
/// </remarks>
public sealed class PromptSnapshotService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        // Dictionary keys ("tbox.extract", etc.) must serialise as-is so the
        // persisted blob matches the Python backend byte-for-byte; the inner
        // entry fields use [JsonPropertyName] so the camel-cased record
        // members land as snake_case JSON properties.
        DictionaryKeyPolicy = null,
    };

    /// <summary>Build the snapshot for the prompts an extraction run consumed.</summary>
    /// <param name="prompts">
    /// Dictionary keyed by prompt name. Order of the resulting JSON object
    /// follows insertion order — the caller passes the prompts in the order
    /// they were loaded (TBox first, ABox second, terminology third, …) so
    /// the snapshot is byte-stable across runs.
    /// </param>
    public JsonDocument SnapshotAsync(IReadOnlyDictionary<string, string> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (key, content) in prompts)
        {
            entries[key] = new Entry(content, HashHex(content), Overridden: false);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Root(entries), SerializerOptions);
        return JsonDocument.Parse(bytes);
    }

    private static string HashHex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record Root(
        [property: JsonPropertyName("prompts")] IReadOnlyDictionary<string, Entry> Prompts);

    private sealed record Entry(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("overridden")] bool Overridden);
}