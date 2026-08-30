using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Llm;
using ISEStudio.Observability;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Prompts;

namespace ISEStudio.Extraction;

/// <summary>
/// LLM call + reply parsing for one ABox (instance) chunk. Mirrors
/// <see cref="TBoxExtractionService"/> but with the instance-oriented prompt
/// vocabulary. Each call asks for evidence-grounded mentions plus the data
/// and object assertions attached to them.
/// </summary>
/// <remarks>
/// <para>The prompt body is resolved at call time from
/// <see cref="PromptLocales"/> against <see cref="ISEStudioOptions.SystemLanguage"/>
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

    private readonly ISEStudioOptions _options;
    private readonly ILogger<ABoxExtractionService> _logger;

    public ABoxExtractionService(
        IOptions<ISEStudioOptions> options,
        ILogger<ABoxExtractionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<ABoxExtractionService>.Instance;
    }

    /// <summary>
    /// Resolve the current system prompt body for <see cref="PromptKey"/>
    /// according to <see cref="ISEStudioOptions.SystemLanguage"/>. See
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
            // Distinct from TBoxExtractionService's "Llm.Extract" so ABox
            // and TBox cancel events stay separable on dashboards — they
            // have different baselines (ABox fires per-chunk after the
            // TBox phase; TBox fires per-chunk first).
            operationName: "Llm.ABoxExtract",
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

                // Stopwatch lets the diagnostic capture how long the call
                // ran before it was cancelled — pairing elapsed seconds with
                // the configured LlmNetworkTimeoutSeconds tells us whether
                // the SDK hit its internal pipeline timeout (NetworkTimeout)
                // versus a user-initiated cancellation. Same shape as
                // TBoxExtractionService.ExtractAsync; per-service
                // operationName ("Llm.ABoxExtract") keeps server-log
                // dashboards separable from the TBox extractor.
                var sw = Stopwatch.StartNew();
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    LlmCallDiagnostics.LogCancellation(
                        _logger,
                        operationName: "Llm.ABoxExtract",
                        provider: provider,
                        model: model,
                        elapsedSeconds: sw.Elapsed.TotalSeconds,
                        configuredTimeoutSec: _options.LlmNetworkTimeoutSeconds,
                        isCallerCancelled: cancellationToken.IsCancellationRequested,
                        exception: oce);
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Transient provider/network error: see TBoxExtractionService.
                    return ABoxDelta.Empty;
                }
                return ExtractionDeltaParser.ParseABox(response.Text);
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
