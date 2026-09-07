using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Documents;

/// <summary>
/// Durable adapter for every document format handled by <see cref="Parsing.IDocumentParser"/>.
/// The processor owns blob loading, parser metadata persistence, versioning, and chunking.
/// </summary>
public sealed class ParserExtractionJobHandler : IExtractionJobHandler
{
    private readonly DocumentIngestionJobProcessor _processor;

    public ParserExtractionJobHandler(DocumentIngestionJobProcessor processor)
    {
        _processor = processor;
    }

    public string Kind => DocumentIngestionJobProcessor.Kind;

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

        var documentId = ExtractionJobPayloadReader.ReadRequiredGuid(
            payload,
            "document_id",
            "documentId");
        var documentSha256 = ExtractionJobPayloadReader.ReadRequiredString(
            payload,
            "document_sha256",
            "documentSha256");
        var model = ExtractionJobPayloadReader.ReadOptionalString(payload, "model") ?? job.Model;

        var result = await _processor.ProcessAsync(
            new DocumentIngestionJob(
                job.Id,
                knowledgeSystemId,
                documentId,
                model,
                documentSha256),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result.Status, JobStatus.Completed.ToWire(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Extraction job '{job.Id}' finished with status '{result.Status}'.");
        }
    }
}
