using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace OnToPilot.Parsing;

/// <summary>
/// Layered parser that prefers DoclingDotNet and degrades to per-format fallbacks.
///
/// <para>Mirrors <c>backend/app/parsing/parser.py</c> for the in-scope extensions
/// (<c>pdf / docx / xlsx / xls / txt / md / markdown / csv</c>). HTML and PPTX throw
/// <see cref="NotSupportedException"/> — the brief keeps them out of scope for Task 2.</para>
/// </summary>
public sealed class DocumentParser : IDocumentParser
{
    // Same set as the Python `SUPPORTED_EXTS`. Order matters only for diagnostics.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "doc", "xlsx", "xls", "txt", "md", "markdown", "csv",
    };

    // Extensions for which we would attempt DoclingDotNet first. We currently only ship a
    // DoclingDotNet 1.2.0 PDF backend; DOCX/XLSX are handled by the lightweight fallback.
    private static readonly HashSet<string> DoclingHandled = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf",
    };

    private readonly ILogger<DocumentParser>? _logger;

    public DocumentParser(ILogger<DocumentParser>? logger = null)
    {
        _logger = logger;
    }

    public ParseResult Parse(Stream content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);

        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!Supported.Contains(ext))
        {
            // HTML/PPTX (and any other unsupported extension) follow the same rule: throw.
            throw new NotSupportedException($"Unsupported file type: .{ext}");
        }

        // Buffer the stream so DoclingDotNet and the fallback can each seek.
        var bytes = ReadAllBytes(content);
        using var buffered = new MemoryStream(bytes, writable: false);

        if (DoclingHandled.Contains(ext))
        {
            try
            {
                var docling = TryDoclingPdf(bytes, fileName);
                if (docling is not null && !string.IsNullOrWhiteSpace(docling.Text))
                {
                    return docling;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DoclingDotNet failed on {File}; falling back", fileName);
            }
        }

        return FallbackParse(bytes, ext, fileName);
    }

    private static byte[] ReadAllBytes(Stream s)
    {
        if (s is MemoryStream ms && ms.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == ms.Length)
        {
            return segment.Array!;
        }
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Try DoclingDotNet's PDF conversion. Returns <c>null</c> if the runtime cannot load
    /// the native dependency (the Windows PDFium runtime is not always present in CI), or
    /// if conversion yields empty text.
    /// </summary>
    private static ParseResult? TryDoclingPdf(byte[] bytes, string fileName)
    {
        // Loading the DoclingDotNet assembly can throw FileNotFoundException / TypeLoadException
        // when the bblanchon.PDFium native runtime is missing. Treat both as "unavailable".
        Type? sessionType;
        try
        {
            sessionType = Type.GetType("DoclingDotNet.Parsing.DoclingParseSession, DoclingDotNet");
        }
        catch
        {
            return null;
        }
        if (sessionType is null) return null;

        try
        {
            dynamic session = Activator.CreateInstance(sessionType)!;
            using (session as IDisposable)
            {
                var key = Guid.NewGuid().ToString("N");
                session.LoadDocumentFromBytes(key, bytes, fileName, null);

                var pageCount = (int)session.GetPageCount(key);
                if (pageCount <= 0) return null;

                var sb = new StringBuilder();
                for (var page = 1; page <= pageCount; page++)
                {
                    var json = (string)session.DecodeSegmentedPageJson(key, page);
                    if (string.IsNullOrEmpty(json)) continue;

                    sb.Append("## Page ").Append(page).Append("\n\n");
                    // The JSON schema is the SegmentedPdfPageDto contract; for the .NET fallback
                    // path we only need a coarse page marker — DoclingDotNet's full markdown
                    // export pipeline lives in docling-core's HybridChunker (Python only).
                    sb.Append(ExtractPageText(json)).Append("\n\n");
                }

                session.UnloadDocument(key);
                return new ParseResult(sb.ToString().TrimEnd(), "docling", null);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Pull text content out of a SegmentedPdfPage JSON envelope using JsonDocument. Best
    /// effort — DoclingDotNet may produce no text if no text cells were decoded.
    /// </summary>
    private static string ExtractPageText(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var lines = new List<string>();
            if (doc.RootElement.TryGetProperty("textlineCells", out var lineCells))
            {
                foreach (var cell in lineCells.EnumerateArray())
                {
                    if (cell.TryGetProperty("text", out var text))
                    {
                        var s = text.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
                    }
                }
            }
            if (lines.Count == 0 && doc.RootElement.TryGetProperty("wordCells", out var wordCells))
            {
                foreach (var cell in wordCells.EnumerateArray())
                {
                    if (cell.TryGetProperty("text", out var text))
                    {
                        var s = text.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
                    }
                }
            }
            return string.Join("\n", lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    // --------------------------------------------------------------------------- //
    // Lightweight fallbacks
    // --------------------------------------------------------------------------- //
    private static ParseResult FallbackParse(byte[] bytes, string ext, string fileName)
    {
        return ext switch
        {
            "pdf" => new ParseResult(FallbackPdf(bytes), "fallback:pdf"),
            "docx" or "doc" => new ParseResult(FallbackDocx(bytes), "fallback:docx"),
            "xlsx" or "xls" => new ParseResult(FallbackXlsx(bytes), "fallback:xlsx"),
            "txt" or "md" or "markdown" or "csv" => new ParseResult(
                Encoding.UTF8.GetString(bytes), "fallback:text"),
            _ => throw new NotSupportedException($"Unsupported file type: .{ext}"),
        };
    }

    private static string FallbackPdf(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(bytes);
        var pages = doc.GetPages().ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            var pageText = pages[i].Text ?? string.Empty;
            sb.Append("## Page ").Append(i + 1).Append('\n').Append(pageText);
            if (i < pages.Count - 1) sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string FallbackDocx(byte[] bytes)
    {
        var paragraphs = new List<string>();
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        foreach (var p in body.Descendants<Paragraph>())
        {
            var text = p.InnerText;
            if (!string.IsNullOrWhiteSpace(text)) paragraphs.Add(text);
        }
        foreach (var table in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>())
        {
            foreach (var row in table.Descendants<TableRow>())
            {
                var cells = row.Descendants<TableCell>()
                    .Select(c => c.InnerText.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (cells.Count > 0) paragraphs.Add(string.Join(" | ", cells));
            }
        }

        return string.Join("\n\n", paragraphs);
    }

    private static string FallbackXlsx(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var sheets = wb.Worksheets.ToList();
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            sb.Append("## Sheet: ").Append(sheet.Name);
            foreach (var row in sheet.RowsUsed())
            {
                var cells = row.CellsUsed()
                    .Select(c => c.GetString().Trim())
                    .ToList();
                if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;
                sb.Append('\n').Append(string.Join("\t", cells));
            }
            if (i < sheets.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }
}