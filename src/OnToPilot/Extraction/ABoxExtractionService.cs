using Microsoft.Extensions.AI;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Parsing;

namespace OnToPilot.Extraction;

/// <summary>
/// LLM call + reply parsing for one ABox (instance) chunk. Mirrors
/// <see cref="TBoxExtractionService"/> but with the instance-oriented prompt
/// vocabulary. Each call asks for evidence-grounded mentions plus the data
/// and object assertions attached to them.
/// </summary>
public sealed class ABoxExtractionService
{
    /// <summary>Prompt registry key for the instance extraction prompt.</summary>
    public const string PromptKey = "abox.extract";

    /// <summary>System prompt sent for every ABox chunk.</summary>
    public const string SystemPrompt = """
        You are reading a chunk of source documentation and identifying named
        individuals (people, places, products, documents, events, …) that are
        instances of classes already declared in the knowledge system's TBox.
        Return only JSON of this exact shape:

        {
          "individuals": [{
            "label": "Exact Name From Source",
            "class": "ExistingClassLabel",
            "evidence": "verbatim span from the chunk",
            "attributes": [{"property": "dataPropertyLabel", "value": "literal"}],
            "relations": [{"property": "objectPropertyLabel", "target": "Label of another individual"}]
          }]
        }

        Skip any mention whose class is not already declared in the TBox.
        Omit any section you have no evidence for (return an empty array).
        """;

    /// <summary>
    /// Send the chunk text to the LLM and parse the reply into an
    /// <see cref="ABoxDelta"/>. The merger is the source of truth for class
    /// resolution: this layer only parses the reply and never talks to the
    /// store.
    /// </summary>
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

        var classListing = existingClassLabels.Count == 0
            ? "(no classes declared yet)"
            : string.Join(", ", existingClassLabels);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
                $"Knowledge system: {ks.GraphIri}\nBase IRI: {ks.BaseIri}\n" +
                $"Existing TBox classes: {classListing}\n\n" +
                $"Chunk #{chunk.Idx} text:\n{chunk.Text}"),
        };

        try
        {
            var response = await chat.GetResponseAsync(messages, options: null, cancellationToken).ConfigureAwait(false);
            return ExtractionDeltaParser.ParseABox(response.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // See TBoxExtractionService.ExtractAsync for the rationale.
            return ABoxDelta.Empty;
        }
    }
}