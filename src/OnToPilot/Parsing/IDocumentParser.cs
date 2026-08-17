namespace OnToPilot.Parsing;

/// <summary>
/// Abstraction over document -> text extraction. The default implementation
/// (<see cref="DocumentParser"/>) prefers DoclingDotNet and falls back to lightweight
/// per-format parsers (PdfPig / DocumentFormat.OpenXml / ClosedXML / plain text).
/// </summary>
public interface IDocumentParser
{
    /// <summary>
    /// Extract text from <paramref name="content"/> using the dispatch rules
    /// registered for <paramref name="fileName"/>'s extension.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the extension is not in <c>backend/app/parsing/parser.py::SUPPORTED_EXTS</c>.
    /// </exception>
    ParseResult Parse(Stream content, string fileName);
}