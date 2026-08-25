using ISEStudio.Parsing;

namespace ISEStudio.Tests.Documents;

/// <summary>
/// Test-only <see cref="IDocumentParser"/> that bypasses DoclingDotNet
/// and PdfPig entirely so parse contract tests don't pull in native
/// binaries. Mirrors <c>DocumentParser.SUPPORTED</c> so unsupported
/// extensions still throw <see cref="NotSupportedException"/> — the
/// negative-path tests assert on the same exception type the production
/// parser would raise.
/// </summary>
public sealed class TestDocumentParser : IDocumentParser
{
    /// <summary>
    /// Mirror of <c>DocumentParser.Supported</c>. Keep in sync if the
    /// production set ever changes.
    /// </summary>
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "doc", "xlsx", "xls", "txt", "md", "markdown", "csv",
    };

    public ParseResult Parse(Stream content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);

        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!Supported.Contains(ext))
        {
            throw new NotSupportedException($"Unsupported file type: .{ext}");
        }

        // Text-shaped formats: stream the bytes back as UTF-8. The stream
        // is left open so the caller can dispose it; tests always supply
        // a MemoryStream that they own.
        if (ext is "txt" or "md" or "markdown" or "csv")
        {
            using var sr = new StreamReader(content, leaveOpen: true);
            var text = sr.ReadToEnd();
            return new ParseResult(text, "test:text", null);
        }

        // Binary formats in tests get a deterministic stub: three
        // newline-separated lines so the chunker has something to split
        // and tests can assert on chunk_count > 0 without depending on
        // DoclingDotNet's exact output.
        var stub =
            $"Test content for {fileName}\n\n" +
            "Line two with enough text to exceed the per-paragraph floor.\n\n" +
            "Line three, also deliberately substantive.";
        return new ParseResult(stub, $"test:{ext}", null);
    }
}