using System.Reflection;
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
///
/// <para>Layering per extension (DoclingDotNet backend → lightweight fallback):</para>
/// <list type="bullet">
///   <item><c>pdf</c>   → <c>DoclingParseSession</c> → <c>PdfPig</c></item>
///   <item><c>docx</c>  → <c>MsWordDocumentBackend</c> → <c>DocumentFormat.OpenXml</c></item>
///   <item><c>xlsx</c>  → <c>MsExcelDocumentBackend</c> → <c>ClosedXML</c></item>
///   <item>plain text  → no structured backend, raw UTF-8 decode</item>
/// </list>
///
/// <para>Each layer degrades gracefully: if DoclingDotNet throws at construction or
/// during <c>ConvertAsync</c>, the parser falls through to the lightweight backend.</para>
/// </summary>
public sealed class DocumentParser : IDocumentParser
{
    // Same set as the Python `SUPPORTED_EXTS`. Order matters only for diagnostics.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "doc", "xlsx", "xls", "txt", "md", "markdown", "csv",
    };

    // Extensions for which we attempt DoclingDotNet first. The corresponding
    // DoclingDotNet backend is discovered at runtime via IDocumentBackend.SupportedExtensions.
    private static readonly HashSet<string> DoclingHandled = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "doc", "xlsx", "xls",
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

        if (DoclingHandled.Contains(ext))
        {
            try
            {
                var docling = TryDocling(bytes, fileName, ext);
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
    /// Try the DoclingDotNet backend matching <paramref name="ext"/>. Returns <c>null</c>
    /// if the runtime cannot load the native dependency (the Windows PDFium runtime is not
    /// always present in CI), if no backend advertises <paramref name="ext"/>, or if
    /// conversion yields empty text.
    /// </summary>
    private static ParseResult? TryDocling(byte[] bytes, string fileName, string ext)
    {
        if (string.Equals(ext, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return TryDoclingPdf(bytes, fileName);
        }
        return TryDoclingBackend(bytes, fileName, ext);
    }

    /// <summary>
    /// Try DoclingDotNet's PDF conversion via <c>DoclingParseSession</c>. Returns <c>null</c>
    /// if the runtime cannot load the native dependency (the Windows PDFium runtime is not
    /// always present in CI), or if conversion yields empty text.
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
    /// Try a DoclingDotNet <c>IDocumentBackend</c> for non-PDF formats. Discovers the
    /// backend by walking exported types whose <c>SupportedExtensions</c> includes
    /// <paramref name="ext"/>. Catches all construction / conversion exceptions and
    /// returns <c>null</c> so the caller can degrade to the lightweight fallback.
    /// </summary>
    private static ParseResult? TryDoclingBackend(byte[] bytes, string fileName, string ext)
    {
        // Locate the DoclingDotNet assembly without forcing it to load — reflection-only
        // inspection avoids TypeLoadException when native deps are missing.
        Assembly? asm;
        try
        {
            asm = Assembly.Load(new AssemblyName("DoclingDotNet"));
        }
        catch
        {
            return null;
        }
        if (asm is null) return null;

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        if (types.Length == 0) return null;

        var backendInterface = types.FirstOrDefault(t => t?.FullName == "DoclingDotNet.Backends.IDocumentBackend");
        if (backendInterface is null) return null;

        var normalized = ext.TrimStart('.').ToLowerInvariant();
        Type? backendType = null;
        foreach (var t in types)
        {
            if (t is null) continue;
            if (!backendInterface.IsAssignableFrom(t)) continue;
            try
            {
                var instance = Activator.CreateInstance(t);
                if (instance is null) continue;
                var extProp = t.GetProperty("SupportedExtensions");
                if (extProp?.GetValue(instance) is not IEnumerable<object> exts) continue;
                if (exts.Any(e => string.Equals(e?.ToString(), normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    backendType = t;
                    break;
                }
            }
            catch
            {
                // Backends can throw in their constructor when native deps are missing.
                continue;
            }
        }
        if (backendType is null) return null;

        try
        {
            dynamic backend = Activator.CreateInstance(backendType)!;
            using var ms = new MemoryStream(bytes, writable: false);
            var task = backend.ConvertAsync(ms, CancellationToken.None);
            task.Wait();
            var pages = (IReadOnlyList<object>?)task.Result;
            if (pages is null || pages.Count == 0) return null;

            var text = ExtractBackendText(pages, normalized);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new ParseResult(text, "docling", null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Concatenate text out of the <c>SegmentedPdfPageDto</c> list returned by a
    /// DoclingDotNet <c>IDocumentBackend.ConvertAsync</c> call. Adds a section marker
    /// per page so the chunker can still see boundaries when the source format is not
    /// paginated natively (e.g. XLSX, where each "page" is a logical sheet slice).
    /// </summary>
    private static string ExtractBackendText(IReadOnlyList<object> pages, string ext)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page is null) continue;

            var pageType = page.GetType();
            var cellsProp = pageType.GetProperty("TextlineCells");
            var cells = cellsProp?.GetValue(page) as IEnumerable<object>;
            var lines = new List<string>();
            if (cells is not null)
            {
                foreach (var cell in cells)
                {
                    if (cell is null) continue;
                    var textProp = cell.GetType().GetProperty("Text");
                    var s = textProp?.GetValue(cell) as string;
                    if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
                }
            }
            if (lines.Count == 0) continue;

            // DoclingDotNet's MsExcelDocumentBackend treats each sheet as one "page"; emit
            // a sheet marker that mirrors the ClosedXML fallback format so downstream code
            // can rely on a single heading convention regardless of backend.
            var sectionLabel = string.Equals(ext, "xlsx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, "xls", StringComparison.OrdinalIgnoreCase)
                ? $"## Sheet: page {i + 1}"
                : $"## Page {i + 1}";
            sb.Append(sectionLabel).Append('\n').Append(string.Join("\n", lines));
            if (i < pages.Count - 1) sb.Append("\n\n");
        }
        return sb.ToString();
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
            // DOCX: DoclingDotNet first → DocumentFormat.OpenXml fallback. The OpenXml
            // path is exercised whenever DoclingDotNet's MsWordDocumentBackend cannot
            // instantiate (e.g. missing native deps on this host).
            "docx" or "doc" => new ParseResult(FallbackDocx(bytes), "fallback:docx"),
            // XLSX: DoclingDotNet first → ClosedXML fallback.
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