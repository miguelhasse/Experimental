using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.IO.Compression;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkItDown.Benchmarks.TestData;

/// <summary>
/// Generates in-memory binary test fixtures for each supported document format.
/// All public methods return a fresh byte array; allocations happen only in [GlobalSetup].
/// </summary>
public static class TestDataFactory
{
    // -------------------------------------------------------------------------
    // Plain text
    // -------------------------------------------------------------------------

    public static byte[] CreatePlainText(int paragraphs = 10)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < paragraphs; i++)
        {
            sb.AppendLine($"Paragraph {i + 1}: The quick brown fox jumps over the lazy dog. " +
                          "Lorem ipsum dolor sit amet, consectetur adipiscing elit.");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // HTML
    // -------------------------------------------------------------------------

    public static byte[] CreateHtml(int sections = 5)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><title>Benchmark Page</title></head><body>");
        for (var i = 0; i < sections; i++)
        {
            sb.AppendLine($"<h2>Section {i + 1}</h2>");
            sb.AppendLine($"<p>The quick brown fox jumps over the lazy dog. " +
                          $"This is paragraph {i + 1} of the benchmark HTML document.</p>");
            sb.AppendLine("<ul><li>Item one</li><li>Item two</li><li>Item three</li></ul>");
        }
        sb.AppendLine("</body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // CSV
    // -------------------------------------------------------------------------

    public static byte[] CreateCsv(int rows = 100, int cols = 5)
    {
        var sb = new StringBuilder();
        // Header row
        sb.AppendLine(string.Join(",", Enumerable.Range(1, cols).Select(c => $"Column{c}")));
        for (var r = 0; r < rows; r++)
        {
            sb.AppendLine(string.Join(",", Enumerable.Range(1, cols).Select(c => $"R{r + 1}C{c}")));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // RSS / Atom
    // -------------------------------------------------------------------------

    public static byte[] CreateRss(int items = 20)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<rss version=\"2.0\"><channel>");
        sb.AppendLine("<title>Benchmark Feed</title>");
        sb.AppendLine("<link>https://example.com</link>");
        sb.AppendLine("<description>A feed used for benchmarking.</description>");
        for (var i = 0; i < items; i++)
        {
            sb.AppendLine($"<item><title>Item {i + 1}</title>" +
                          $"<link>https://example.com/item/{i + 1}</link>" +
                          $"<description>Description for item {i + 1}.</description></item>");
        }
        sb.AppendLine("</channel></rss>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // DOCX (WordprocessingML)
    // -------------------------------------------------------------------------

    public static byte[] CreateDocx(int paragraphs = 10)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new W.Body();

            body.Append(new W.Paragraph(
                new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                new W.Run(new W.Text("Benchmark Document"))));

            for (var i = 0; i < paragraphs; i++)
            {
                body.Append(new W.Paragraph(
                    new W.Run(new W.Text($"Paragraph {i + 1}: The quick brown fox jumps over the lazy dog."))));
            }

            // A small table
            var table = new W.Table();
            var headerRow = new W.TableRow();
            foreach (var col in new[] { "Name", "Value", "Notes" })
            {
                headerRow.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text(col)))));
            }
            table.Append(headerRow);
            for (var r = 0; r < 5; r++)
            {
                var row = new W.TableRow();
                row.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text($"Item {r + 1}")))));
                row.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text((r * 100).ToString())))));
                row.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text($"Note {r + 1}")))));
                table.Append(row);
            }
            body.Append(table);

            mainPart.Document = new W.Document(body);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // XLSX (SpreadsheetML)
    // -------------------------------------------------------------------------

    public static byte[] CreateXlsx(int rows = 50, int cols = 6)
    {
        cols = Math.Min(cols, 26); // cell references use single letters A-Z
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new SS.Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SS.SheetData();

            // Header row — use InlineString so no SharedStringTable is needed
            var headerRow = new SS.Row { RowIndex = 1 };
            for (var c = 0; c < cols; c++)
            {
                headerRow.Append(new SS.Cell
                {
                    CellReference = $"{(char)('A' + c)}1",
                    DataType = SS.CellValues.InlineString,
                    InlineString = new SS.InlineString(new SS.Text($"Col{c + 1}"))
                });
            }
            sheetData.Append(headerRow);

            for (var r = 0; r < rows; r++)
            {
                var row = new SS.Row { RowIndex = (uint)(r + 2) };
                for (var c = 0; c < cols; c++)
                {
                    row.Append(new SS.Cell
                    {
                        CellReference = $"{(char)('A' + c)}{r + 2}",
                        CellValue = new SS.CellValue((r * cols + c + 1).ToString())
                    });
                }
                sheetData.Append(row);
            }

            worksheetPart.Worksheet = new SS.Worksheet(sheetData);
            worksheetPart.Worksheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new SS.Sheets());
            sheets.Append(new SS.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Data"
            });
            workbookPart.Workbook.Save();
        }

        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // PPTX (PresentationML)
    // -------------------------------------------------------------------------

    public static byte[] CreatePptx(int slides = 5)
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            var slideIdList = new P.SlideIdList();

            for (var i = 0; i < slides; i++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();

                var titleShape = new P.Shape(
                    new P.NonVisualShapeProperties(
                        new A.NonVisualDrawingProperties { Id = 2U, Name = $"Title {i + 1}" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties(
                            new P.PlaceholderShape { Type = P.PlaceholderValues.Title })),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new A.BodyProperties(),
                        new A.Paragraph(new A.Run(new A.Text($"Slide {i + 1}")))));

                var bodyShape = new P.Shape(
                    new P.NonVisualShapeProperties(
                        new A.NonVisualDrawingProperties { Id = 3U, Name = "Content" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new A.BodyProperties(),
                        new A.Paragraph(new A.Run(new A.Text($"Content for benchmark slide {i + 1}.")))));

                var shapeTree = new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new A.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new A.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()),
                    titleShape,
                    bodyShape);

                slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
                slidePart.Slide.Save();

                var rId = presentationPart.GetIdOfPart(slidePart);
                slideIdList.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = rId });
            }

            presentationPart.Presentation = new P.Presentation(slideIdList);
            presentationPart.Presentation.Save();
        }

        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // PDF  (minimal hand-crafted PDF-1.4 with correct xref offsets)
    // -------------------------------------------------------------------------

    public static byte[] CreatePdf(int pages = 3)
    {
        const string pageText = "The quick brown fox jumps over the lazy dog. Benchmark PDF content.";

        // Object numbering:
        //   1          = Catalog
        //   2          = Pages dictionary
        //   3..2+pages = Page objects
        //   3+pages..2+2*pages = Content streams
        //   3+2*pages  = Font
        var totalObjects = 3 + 2 * pages;
        var fontObjNum = 3 + 2 * pages;
        var kids = string.Join(" ", Enumerable.Range(3, pages).Select(i => $"{i} 0 R"));

        // Build each object's string
        var objs = new string[totalObjects + 1]; // 1-indexed
        objs[1] = $"1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n";
        objs[2] = $"2 0 obj\n<</Type/Pages/Kids[{kids}]/Count {pages}>>\nendobj\n";

        for (var p = 0; p < pages; p++)
        {
            var pageObjNum = 3 + p;
            var contentObjNum = 3 + pages + p;
            objs[pageObjNum] =
                $"{pageObjNum} 0 obj\n<</Type/Page/Parent 2 0 R" +
                $"/MediaBox[0 0 612 792]" +
                $"/Contents {contentObjNum} 0 R" +
                $"/Resources<</Font<</F1 {fontObjNum} 0 R>>>>>>\nendobj\n";

            var stream = $"BT /F1 12 Tf 72 720 Td ({pageText} — page {p + 1}) Tj ET\n";
            objs[contentObjNum] =
                $"{contentObjNum} 0 obj\n<</Length {stream.Length}>>\nstream\n{stream}endstream\nendobj\n";
        }

        objs[fontObjNum] =
            $"{fontObjNum} 0 obj\n<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>\nendobj\n";

        // Compute byte offsets (all text is ASCII so char count == byte count)
        const string header = "%PDF-1.4\n";
        var offsets = new int[totalObjects + 1];
        var pos = header.Length;
        for (var i = 1; i <= totalObjects; i++)
        {
            offsets[i] = pos;
            pos += objs[i].Length;
        }
        var xrefOffset = pos;

        // Assemble final output
        var sb = new StringBuilder(pos + 256);
        sb.Append(header);
        for (var i = 1; i <= totalObjects; i++)
        {
            sb.Append(objs[i]);
        }

        sb.Append("xref\n");
        sb.Append($"0 {totalObjects + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i <= totalObjects; i++)
        {
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<</Size {totalObjects + 1}/Root 1 0 R>>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Image  (1×1 white BMP — smallest lossless image MetadataExtractor accepts)
    // -------------------------------------------------------------------------

    public static byte[] CreateBmp()
    {
        // Minimal 1×1 24-bit BMP (58 bytes total)
        return [
            0x42, 0x4D,              // "BM" signature
            0x3A, 0x00, 0x00, 0x00, // file size = 58 bytes
            0x00, 0x00, 0x00, 0x00, // reserved
            0x36, 0x00, 0x00, 0x00, // pixel data offset = 54
            0x28, 0x00, 0x00, 0x00, // BITMAPINFOHEADER size = 40
            0x01, 0x00, 0x00, 0x00, // width = 1
            0x01, 0x00, 0x00, 0x00, // height = 1
            0x01, 0x00,             // color planes = 1
            0x18, 0x00,             // bits per pixel = 24
            0x00, 0x00, 0x00, 0x00, // compression = BI_RGB (none)
            0x04, 0x00, 0x00, 0x00, // raw image size (with padding)
            0x13, 0x0B, 0x00, 0x00, // horizontal resolution (2835 px/m)
            0x13, 0x0B, 0x00, 0x00, // vertical resolution
            0x00, 0x00, 0x00, 0x00, // colors in table
            0x00, 0x00, 0x00, 0x00, // important colors
            0xFF, 0xFF, 0xFF,       // pixel: white (BGR)
            0x00                    // row padding to 4-byte boundary
        ];
    }

    // -------------------------------------------------------------------------
    // ZIP  (contains a plain text file per entry)
    // -------------------------------------------------------------------------

    public static byte[] CreateZip(int entries = 5)
    {
        using var ms = new MemoryStream();
        using var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true);
        for (var i = 0; i < entries; i++)
        {
            var entry = zip.CreateEntry($"file{i + 1:D2}.txt", CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine($"# File {i + 1}");
            writer.WriteLine();
            writer.Write("The quick brown fox jumps over the lazy dog. " +
                         "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
                         $"This is entry {i + 1} of the benchmark ZIP archive.");
        }
        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // EPUB  (valid EPUB 2 container with VersOne.Epub-compatible structure)
    // -------------------------------------------------------------------------

    public static byte[] CreateEpub(int chapters = 3)
    {
        using var ms = new MemoryStream();
        // ZipArchiveMode.Create — must be left open until we return the bytes
        using var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true);

        // 1. mimetype — MUST be first and uncompressed
        WriteEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

        // 2. META-INF/container.xml
        WriteEntry(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        // 3. OEBPS/content.opf
        var manifestItems = new StringBuilder();
        var spineItems = new StringBuilder();
        for (var i = 1; i <= chapters; i++)
        {
            manifestItems.AppendLine(
                $"<item id=\"ch{i}\" href=\"chapter{i:D2}.html\" media-type=\"application/xhtml+xml\"/>");
            spineItems.AppendLine($"<itemref idref=\"ch{i}\"/>");
        }

        WriteEntry(zip, "OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Benchmark Book</dc:title>
                <dc:identifier id="uid">benchmark-book-001</dc:identifier>
              </metadata>
              <manifest>
                {manifestItems}
              </manifest>
              <spine>
                {spineItems}
              </spine>
            </package>
            """);

        // 4. Chapter HTML files
        for (var i = 1; i <= chapters; i++)
        {
            WriteEntry(zip, $"OEBPS/chapter{i:D2}.html", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN"
                    "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <head><title>Chapter {i}</title></head>
                  <body>
                    <h1>Chapter {i}</h1>
                    <p>The quick brown fox jumps over the lazy dog.
                       This is benchmark chapter {i} with sample content.</p>
                  </body>
                </html>
                """);
        }

        return ms.ToArray();

        static void WriteEntry(ZipArchive archive, string name, string content,
            CompressionLevel level = CompressionLevel.Fastest)
        {
            var entry = archive.CreateEntry(name, level);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    // -------------------------------------------------------------------------
    // MOBI  (minimal PalmDB + PalmDoc, uncompressed, 2 records)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a minimal valid MOBI file with the specified number of HTML sections
    /// packed into a single PalmDoc text record (uncompressed, UTF-8).
    /// </summary>
    public static byte[] CreateMobi(int sections = 3)
    {
        var html = new StringBuilder();
        html.Append("<html><head><title>Benchmark Book</title></head><body>");
        for (var i = 1; i <= sections; i++)
        {
            html.Append($"<h1>Chapter {i}</h1>");
            html.Append($"<p>The quick brown fox jumps over the lazy dog. " +
                        $"This is benchmark chapter {i} with sample MOBI content.</p>");
        }
        html.Append("</body></html>");

        const string title = "Benchmark Book";
        var titleBytes = Encoding.UTF8.GetBytes(title);
        var contentBytes = Encoding.UTF8.GetBytes(html.ToString());

        // Layout matches TestDocumentFactory.CreateMobiStream (2 records, no compression).
        const int record0Start = 96;
        const int fullNameOffset = 248; // 16 (PalmDoc) + 232 (MOBI)

        var record0Size = fullNameOffset + titleBytes.Length;
        var record1Start = record0Start + record0Size;
        var totalSize = record1Start + contentBytes.Length;

        var buf = new byte[totalSize];

        var nameLen = Math.Min(titleBytes.Length, 31);
        Array.Copy(titleBytes, buf, nameLen);

        buf[60] = (byte)'B'; buf[61] = (byte)'O'; buf[62] = (byte)'O'; buf[63] = (byte)'K';
        buf[64] = (byte)'M'; buf[65] = (byte)'O'; buf[66] = (byte)'B'; buf[67] = (byte)'I';
        buf[76] = 0; buf[77] = 2; // NumRecords = 2

        WriteU32BE(buf, 78, (uint)record0Start);
        WriteU32BE(buf, 86, (uint)record1Start);
        buf[91] = 1;

        WriteU16BE(buf, 96, 1);                           // compression = none
        WriteU32BE(buf, 100, (uint)contentBytes.Length);  // text length
        WriteU16BE(buf, 104, 1);                          // record count
        WriteU16BE(buf, 106, 4096);                       // record size

        buf[112] = (byte)'M'; buf[113] = (byte)'O'; buf[114] = (byte)'B'; buf[115] = (byte)'I';
        WriteU32BE(buf, 116, 232);
        WriteU32BE(buf, 120, 2);
        WriteU32BE(buf, 124, 65001);
        WriteU32BE(buf, 132, 6);

        for (var off = 136; off <= 172; off += 4)
            WriteU32BE(buf, off, 0xFFFFFFFF);

        WriteU32BE(buf, 176, 2);
        WriteU32BE(buf, 180, (uint)fullNameOffset);
        WriteU32BE(buf, 184, (uint)titleBytes.Length);

        Array.Copy(titleBytes, 0, buf, record0Start + fullNameOffset, titleBytes.Length);
        Array.Copy(contentBytes, 0, buf, record1Start, contentBytes.Length);

        return buf;
    }

    private static void WriteU32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteU16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }
}
