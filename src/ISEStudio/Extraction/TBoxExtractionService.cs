using System.ClientModel;
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
/// LLM call + reply parsing for one TBox (schema) chunk. Wraps the chat
/// client behind the extraction service boundary so the orchestrator does
/// not depend on Microsoft.Extensions.AI directly.
/// </summary>
/// <remarks>
/// <para>The system prompt intentionally leaves the LLM free to return only
/// the fields it can defend with evidence — empty arrays are valid for any
/// section. The merge tolerates the same: a chunk that returns nothing
/// contributes zero counters.</para>
/// <para>The body of the prompt is resolved at call time from
/// <see cref="PromptLocales"/> against <see cref="ISEStudioOptions.SystemLanguage"/>
/// so the .NET backend can switch between English and Simplified Chinese
/// at runtime without a recompile. The Python parity key is
/// <c>tbox.extract.rag</c> (see <c>backend/app/ontology/extract.py</c>).</para>
/// </remarks>
public sealed class TBoxExtractionService
{
    /// <summary>
    /// Prompt registry key for the schema extraction prompt. Matches the
    /// Python backend's <c>prompt_config</c> registry exactly so a future
    /// prompt-snapshot auditor can compare the two stacks verbatim.
    /// </summary>
    public const string PromptKey = "tbox.extract.rag";

    private readonly ISEStudioOptions _options;
    private readonly ILogger<TBoxExtractionService> _logger;

    public TBoxExtractionService(
        IOptions<ISEStudioOptions> options,
        ILogger<TBoxExtractionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<TBoxExtractionService>.Instance;
    }

    /// <summary>
    /// Resolve the current system prompt body for
    /// <see cref="PromptKey"/> according to
    /// <see cref="ISEStudioOptions.SystemLanguage"/>. Falls back to the
    /// English default when the key is unknown (it never is, today — the
    /// defensive null-check makes a future rename a build break rather
    /// than a runtime surprise).
    /// </summary>
    /// <remarks>
    /// <see cref="ExtractionOrchestrator"/> reads this method when building
    /// the per-job prompt snapshot persisted on
    /// <c>ExtractionJobEntity.PromptSnapshot</c> — the snapshot must capture
    /// the body that was actually sent, so we resolve it here rather than
    /// reading a baked-in <c>const string</c> at the call site.
    /// </remarks>
    public string ResolveSystemPrompt()
    {
        var lang = PromptLocales.ParseSystemLanguage(_options.SystemLanguage);
        return PromptLocales.ResolveWithFallback(PromptKey, lang)
            ?? throw new InvalidOperationException(
                $"Prompt key '{PromptKey}' is not registered in PromptLocales. " +
                "Add an entry to PromptLocales._byKey before shipping.");
    }

    /// <summary>
    /// Send the chunk text to the LLM and parse the reply into a
    /// <see cref="TBoxDelta"/>. Errors from the chat client bubble; a reply
    /// with no recoverable JSON becomes <see cref="TBoxDelta.Empty"/>.
    /// </summary>
    /// <remarks>
    /// <para>Transient provider failures (<see cref="HttpRequestException"/>,
    /// <see cref="IOException"/>) are tolerated: a flaky LLM must not abort
    /// the whole job, so the chunk is skipped and an empty delta is returned.
    /// Cancellation propagates so the orchestrator's cancellation path runs.
    /// Every other exception (auth failure, configuration error, malformed
    /// SDK reply, …) propagates to <see cref="ExtractionOrchestrator"/>'s
    /// outer <c>catch</c> which reverts the per-phase RDF capture and marks
    /// the job failed — silently returning <see cref="TBoxDelta.Empty"/>
    /// would hide the failure as a successful empty extraction.</para>
    /// </remarks>
    public async Task<TBoxDelta> ExtractAsync(
        IChatClient chat,
        KsContext ks,
        ChunkSpan chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(chunk);

        var provider = ResolveProvider(chat);
        var model = ResolveModel(chat);
        var systemPrompt = ResolveSystemPrompt();

        return await Telemetry.LlmSource.WithLlmActivity(
            operationName: "Llm.Extract",
            provider: provider,
            model: model,
            action: async ct =>
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User,
                        $"Knowledge system: {ks.GraphIri}\nBase IRI: {ks.BaseIri}\nChunk #{chunk.Idx} text:\n{chunk.Text}"),
                };

                // Stopwatch lets the diagnostic capture how long the call ran
                // before it was cancelled — pairing elapsed seconds with the
                // configured LlmNetworkTimeoutSeconds tells us whether the SDK
                // hit its internal pipeline timeout (NetworkTimeout) versus a
                // user-initiated cancellation. Without this we'd see "Cancelled
                // (TaskCanceledException)." with no clue whether to bump the
                // timeout or chase the user.
                var sw = Stopwatch.StartNew();
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    // Shared observability helper: server-log one-liner carries
                    // elapsed + configured timeout + caller-cancel + exception
                    // type so the next "Cancelled (TaskCanceledException)." job
                    // tells us whether to bump the SDK timeout or chase the
                    // user. See LlmCallDiagnostics for the field semantics.
                    LlmCallDiagnostics.LogCancellation(
                        _logger,
                        operationName: "Llm.Extract",
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
                    // Transient provider/network error: log via the orchestrator's
                    // progress channel and skip this chunk.
                    return TBoxDelta.Empty;
                }

                return ExtractionDeltaParser.ParseTBox(response.Text);
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
