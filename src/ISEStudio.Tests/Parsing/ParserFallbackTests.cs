using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISEStudio.Parsing;

namespace ISEStudio.Tests.Parsing;

/// <summary>
/// Tests for the layered parser's fallback behaviour. The brief scopes the .NET port to
/// PDF/DOCX/XLSX/plain-text; HTML and PPTX must throw <see cref="NotSupportedException"/>.
/// DoclingDotNet is the preferred backend; if it cannot process a given input we degrade
/// to the lightweight per-format fallback.
/// </summary>
public sealed class ParserFallbackTests
{
    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_pdf_falls_back_to_PdfPig_when_Docling_unavailable()
    {
        // A minimal one-page PDF (no Docling-friendly metadata). PdfPig must extract at
        // least the page heading so the caller can index the page boundary.
        var pdfBytes = BuildMinimalPdf();

        var parser = new DocumentParser();
        using var ms = new MemoryStream(pdfBytes);
        var result = parser.Parse(ms, "sample.pdf");

        Assert.NotNull(result);
        Assert.NotNull(result.Text);
        Assert.Contains("## Page", result.Text);
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_xlsx_emits_sheet_heading()
    {
        var xlsxBytes = BuildMinimalXlsx();

        var parser = new DocumentParser();
        using var ms = new MemoryStream(xlsxBytes);
        var result = parser.Parse(ms, "workbook.xlsx");

        Assert.NotNull(result);
        Assert.NotNull(result.Text);
        Assert.Contains("## Sheet:", result.Text);
        Assert.Contains("Sheet1", result.Text);
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_docx_falls_back_to_OpenXml_when_Docling_unavailable()
    {
        // DoclingDotNet's MsWordDocumentBackend cannot instantiate on this host (the
        // native PDFium runtime is not present), so the parser must degrade to the
        // DocumentFormat.OpenXml fallback. The OpenXml path emits paragraphs separated by
        // blank lines and should preserve the inserted text verbatim.
        var docxBytes = BuildMinimalDocx("Alpha paragraph.", "Bravo paragraph.");

        var parser = new DocumentParser();
        using var ms = new MemoryStream(docxBytes);
        var result = parser.Parse(ms, "letter.docx");

        Assert.NotNull(result);
        Assert.NotNull(result.Text);
        Assert.Contains("fallback:docx", result.Backend);
        Assert.Contains("Alpha paragraph.", result.Text);
        Assert.Contains("Bravo paragraph.", result.Text);
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_html_throws_NotSupportedException()
    {
        var parser = new DocumentParser();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("<html></html>"));
        Assert.Throws<NotSupportedException>(() => parser.Parse(ms, "page.html"));
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_pptx_throws_NotSupportedException()
    {
        var parser = new DocumentParser();
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        Assert.Throws<NotSupportedException>(() => parser.Parse(ms, "deck.pptx"));
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Parse_txt_returns_decoded_text()
    {
        var parser = new DocumentParser();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello world\n\nsecond paragraph"));
        var result = parser.Parse(ms, "notes.txt");

        Assert.Equal("hello world\n\nsecond paragraph", result.Text);
    }

    /// <summary>
    /// Build a one-page PDF with the absolute minimum structure PdfPig accepts. We only need
    /// a valid header / object table so PdfPig can open it; the actual page text does not
    /// matter for this test (we only assert the <c>## Page N</c> heading appears).
    /// </summary>
    private static byte[] BuildMinimalPdf()
    {
        // Use PdfPig's own writer if available; otherwise embed a hand-crafted minimal PDF.
        // The hand-crafted version is enough to exercise PdfPig's page iterator.
        var content = """
            %PDF-1.4
            1 0 obj
            << /Type /Catalog /Pages 2 0 R >>
            endobj
            2 0 obj
            << /Type /Pages /Kids [3 0 R] /Count 1 >>
            endobj
            3 0 obj
            << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
            endobj
            4 0 obj
            << /Length 44 >>
            stream
            BT /F1 12 Tf 50 750 Td (Fallback page text) Tj ET
            endstream
            endobj
            5 0 obj
            << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
            endobj
            xref
            0 6
            0000000000 65535 f
            0000000010 00000 n
            0000000060 00000 n
            0000000111 00000 n
            0000000211 00000 n
            0000000304 00000 n
            trailer
            << /Size 6 /Root 1 0 R >>
            startxref
            373
            %%EOF
            """;
        return Encoding.ASCII.GetBytes(content);
    }

    private static byte[] BuildMinimalXlsx()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Sheet1");
        sheet.Cell(1, 1).Value = "Alpha";
        sheet.Cell(1, 2).Value = "Bravo";
        sheet.Cell(2, 1).Value = "1";
        sheet.Cell(2, 2).Value = "2";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a minimal DOCX containing the given paragraphs using DocumentFormat.OpenXml.
    /// The output is a real Word document that the OpenXml fallback can read.
    /// </summary>
    private static byte[] BuildMinimalDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            foreach (var text in paragraphs)
            {
                var run = new Run(new Text(text));
                body.AppendChild(new Paragraph(run));
            }
            main.Document.Save();
        }
        return ms.ToArray();
    }
}