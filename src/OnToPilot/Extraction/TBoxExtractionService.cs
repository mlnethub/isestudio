using System.ClientModel;
using Microsoft.Extensions.AI;
using OnToPilot.Llm;
using OnToPilot.Observability;
using OnToPilot.Ontology;
using OnToPilot.Parsing;

namespace OnToPilot.Extraction;

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
/// </remarks>
public sealed class TBoxExtractionService
{
    /// <summary>Prompt registry key for the schema extraction prompt.</summary>
    public const string PromptKey = "tbox.extract";

    /// <summary>System prompt sent for every TBox chunk.</summary>
    public const string SystemPrompt = """
        You are an ontology engineer reading a chunk of source documentation.
        Return only JSON of this exact shape:

        {
          "classes": [{"label": "PascalCase", "comment": "..."}],
          "object_properties": [{"label": "camelCase", "domain": "Class", "range": "Class", "comment": "..."}],
          "data_properties": [{"label": "camelCase", "domain": "Class", "range": "string|integer|decimal|boolean|date|dateTime", "comment": "..."}],
          "subclass_of": [{"sub": "Child", "super": "Parent"}],
          "disjoint_with": [{"a": "ClassA", "b": "ClassB"}],
          "equivalent_class": [{"a": "ClassA", "b": "ClassB"}]
        }

        Omit any section you have no evidence for (return an empty array).
        """;

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

        return await Telemetry.LlmSource.WithLlmActivity(
            operationName: "Llm.Extract",
            provider: provider,
            model: model,
            action: async ct =>
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, SystemPrompt),
                    new(ChatRole.User,
                        $"Knowledge system: {ks.GraphIri}\nBase IRI: {ks.BaseIri}\nChunk #{chunk.Idx} text:\n{chunk.Text}"),
                };

                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
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