using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Llm;
using OnToPilot.Observability;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Prompts;

namespace OnToPilot.Extraction;

/// <summary>
/// LLM call + reply parsing for one ABox (instance) chunk. Mirrors
/// <see cref="TBoxExtractionService"/> but with the instance-oriented prompt
/// vocabulary. Each call asks for evidence-grounded mentions plus the data
/// and object assertions attached to them.
/// </summary>
/// <remarks>
/// <para>The prompt body is resolved at call time from
/// <see cref="PromptLocales"/> against <see cref="OnToPilotOptions.SystemLanguage"/>
/// so the .NET backend can switch between English and Simplified Chinese
/// at runtime without a recompile. The Python parity key is
/// <c>abox.extract</c> (see <c>backend/app/ontology/abox_extract.py</c>).</para>
/// </remarks>
public sealed class ABoxExtractionService
{
    /// <summary>
    /// Prompt registry key for the instance extraction prompt. Matches the
    /// Python backend's <c>prompt_config</c> registry exactly so a future
    /// prompt-snapshot auditor can compare the two stacks verbatim.
    /// </summary>
    public const string PromptKey = "abox.extract";

    private readonly OnToPilotOptions _options;

    public ABoxExtractionService(IOptions<OnToPilotOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>
    /// Resolve the current system prompt body for <see cref="PromptKey"/>
    /// according to <see cref="OnToPilotOptions.SystemLanguage"/>. See
    /// <see cref="TBoxExtractionService.ResolveSystemPrompt"/> for the
    /// rationale; this is the ABox-side mirror.
    /// </summary>
    public string ResolveSystemPrompt()
    {
        var lang = PromptLocales.ParseSystemLanguage(_options.SystemLanguage);
        return PromptLocales.ResolveWithFallback(PromptKey, lang)
            ?? throw new InvalidOperationException(
                $"Prompt key '{PromptKey}' is not registered in PromptLocales. " +
                "Add an entry to PromptLocales._byKey before shipping.");
    }

    /// <summary>
    /// Send the chunk text to the LLM and parse the reply into an
    /// <see cref="ABoxDelta"/>. The merger is the source of truth for class
    /// resolution: this layer only parses the reply and never talks to the
    /// store.
    /// </summary>
    /// <remarks>
    /// <para>Transient provider failures (<see cref="HttpRequestException"/>,
    /// <see cref="IOException"/>) are tolerated; cancellation propagates;
    /// every other exception propagates to <see cref="ExtractionOrchestrator"/>'s
    /// outer <c>catch</c> which reverts the per-phase RDF capture and marks
    /// the job failed. See <see cref="TBoxExtractionService.ExtractAsync"/>
    /// for the full rationale.</para>
    /// </remarks>
    public async Task<ABoxDelta> ExtractAsync(
        IChatClient chat,
        KsContext ks,
        ChunkSpan chunk,
        IReadOnlyCollection<string> existingClassLabels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(existingClassLabels);

        var provider = ResolveProvider(chat);
        var model = ResolveModel(chat);
        var systemPrompt = ResolveSystemPrompt();

        return await Telemetry.LlmSource.WithLlmActivity(
            operationName: "Llm.Extract",
            provider: provider,
            model: model,
            action: async ct =>
            {
                var classListing = existingClassLabels.Count == 0
                    ? "(no classes declared yet)"
                    : string.Join(", ", existingClassLabels);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User,
                        $"Knowledge system: {ks.GraphIri}\nBase IRI: {ks.BaseIri}\n" +
                        $"Existing TBox classes: {classListing}\n\n" +
                        $"Chunk #{chunk.Idx} text:\n{chunk.Text}"),
                };

                try
                {
                    var response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                    return ExtractionDeltaParser.ParseABox(response.Text);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Transient provider/network error: see TBoxExtractionService.
                    return ABoxDelta.Empty;
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveProvider(IChatClient chat)
    {
        var metadata = chat.GetService<ChatClientMetadata>();
        return metadata?.ProviderName ?? "unknown";
    }

    private static string ResolveModel(IChatClient chat)
    {
        var metadata = chat.GetService<ChatClientMetadata>();
        return metadata?.DefaultModelId ?? "unknown";
    }
}
