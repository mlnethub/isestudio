using System.Text.Json;
using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

public sealed class ExtractionJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExtractionJobStore _jobs;

    public ExtractionJobDispatcher(IServiceScopeFactory scopeFactory, ExtractionJobStore jobs)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
    }

    public async Task DispatchAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            if (!string.Equals(job.Kind, PlainTextIngestionJobProcessor.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported extraction job kind '{job.Kind}'.");
            }

            var payload = job.Payload?.RootElement ?? throw new InvalidOperationException(
                $"Extraction job '{job.Id}' is missing a payload.");
            var knowledgeSystemId = ReadRequiredGuid(payload, "knowledge_system_id", "knowledgeSystemId");
            if (knowledgeSystemId != job.KnowledgeSystemId)
            {
                throw new InvalidOperationException(
                    $"Extraction job '{job.Id}' payload knowledge system '{knowledgeSystemId}' does not match the claimed row.");
            }

            var documentId = ReadRequiredGuid(payload, "document_id", "documentId");
            var model = ReadOptionalString(payload, "model") ?? job.Model;

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<DocumentIngestionJobProcessor>();
            var result = await processor.ProcessAsync(
                new DocumentIngestionJob(job.Id, knowledgeSystemId, documentId, model),
                cancellationToken).ConfigureAwait(false);

            if (!string.Equals(result.Status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Extraction job '{job.Id}' finished with status '{result.Status}'.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await _jobs.MarkFailedAsync(job.Id, exception.Message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort terminal write; preserve the original exception for the caller.
            }

            throw;
        }
    }

    private static Guid ReadRequiredGuid(JsonElement payload, params string[] names)
    {
        var text = ReadRequiredString(payload, names);
        if (Guid.TryParse(text, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Payload field '{names[0]}' is not a valid GUID.");
    }

    private static string ReadRequiredString(JsonElement payload, params string[] names)
    {
        var value = ReadOptionalString(payload, names);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Extraction payload is missing required field '{names[0]}'.");
    }

    private static string? ReadOptionalString(JsonElement payload, params string[] names)
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