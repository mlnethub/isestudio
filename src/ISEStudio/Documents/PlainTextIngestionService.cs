using System.Security.Cryptography;
using System.Text;
using ISEStudio.Application.Documents;
using ISEStudio.Parsing;

namespace ISEStudio.Documents;

/// <summary>
/// Converts already-available plain text into an immutable document version.
/// Parsing, source retrieval, jobs, and transport remain outside this boundary.
/// </summary>
public sealed class PlainTextIngestionService
{
    private readonly DocumentVersionStore _versions;
    private readonly Chunker _chunker;

    public PlainTextIngestionService(DocumentVersionStore versions, Chunker chunker)
    {
        _versions = versions;
        _chunker = chunker;
    }

    public Task<DocumentVersionResult> IngestAsync(
        Guid knowledgeSystemId,
        Guid documentId,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var contentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        var chunks = _chunker.Chunk(content)
            .Select(chunk => new DocumentVersionChunkInput(
                chunk.Idx,
                chunk.Text,
                chunk.CharStart,
                chunk.CharEnd,
                chunk.TokenEstimate))
            .ToList();

        return _versions.RecordAsync(
            new DocumentVersionInput(
                knowledgeSystemId,
                documentId,
                contentSha256,
                chunks),
            cancellationToken);
    }
}