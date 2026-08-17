namespace OnToPilot.Parsing;

/// <summary>
/// Result of extracting text from a single uploaded document.
///
/// <para>
/// The contract mirrors <c>backend/app/parsing/parser.py::ParseResult</c>:
/// <c>text</c> is the (possibly multi-page, possibly markdown) plain text the chunker
/// should operate on, <c>backend</c> names which backend produced it (DoclingDotNet or one
/// of the <c>fallback:*</c> formats), and <c>structuredDocument</c> carries the
/// layout-aware Docling document when available so the <see cref="Chunker"/> can prefer
/// structure-aware chunking.
/// </para>
/// </summary>
public sealed record ParseResult(
    string Text,
    string Backend,
    object? StructuredDocument = null);