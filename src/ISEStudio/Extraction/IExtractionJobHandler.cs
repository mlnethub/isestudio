using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

public interface IExtractionJobHandler
{
    string Kind { get; }

    Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken);
}

internal static class ExtractionJobPayloadReader
{
    public static JsonElement ReadPayload(ExtractionJobEntity job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Payload?.RootElement ?? throw new InvalidOperationException(
            $"Extraction job '{job.Id}' is missing a payload.");
    }

    public static Guid ReadRequiredGuid(JsonElement payload, params string[] names)
    {
        var text = ReadRequiredString(payload, names);
        if (Guid.TryParse(text, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Payload field '{names[0]}' is not a valid GUID.");
    }

    public static string ReadRequiredString(JsonElement payload, params string[] names)
    {
        var value = ReadOptionalString(payload, names);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Extraction payload is missing required field '{names[0]}'.");
    }

    public static string? ReadOptionalString(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.Null => null,
                    _ => element.GetRawText(),
                };
            }
        }

        return null;
    }
}