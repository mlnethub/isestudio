using ISEStudio.Documents;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

public sealed class PlainTextExtractionJobHandler : IExtractionJobHandler
{
    private readonly DocumentIngestionJobProcessor _processor;

    public PlainTextExtractionJobHandler(DocumentIngestionJobProcessor processor)
    {
        _processor = processor;
    }

    public string Kind => PlainTextIngestionJobProcessor.Kind;

    public async Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = ExtractionJobPayloadReader.ReadPayload(job);
        var knowledgeSystemId = ExtractionJobPayloadReader.ReadRequiredGuid(
            payload,
            "knowledge_system_id",
            "knowledgeSystemId");
        if (knowledgeSystemId != job.KnowledgeSystemId)
        {
            throw new InvalidOperationException(
                $"Extraction job '{job.Id}' payload knowledge system '{knowledgeSystemId}' does not match the claimed row.");
        }

        var documentId = ExtractionJobPayloadReader.ReadRequiredGuid(payload, "document_id", "documentId");
        var model = ExtractionJobPayloadReader.ReadOptionalString(payload, "model") ?? job.Model;

        var result = await _processor.ProcessAsync(
            new DocumentIngestionJob(job.Id, knowledgeSystemId, documentId, model),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result.Status, JobStatus.Completed.ToWire(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Extraction job '{job.Id}' finished with status '{result.Status}'.");
        }
    }
}