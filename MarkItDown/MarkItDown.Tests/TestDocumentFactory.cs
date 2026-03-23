using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.IO.Compression;
using System.Text;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace MarkItDown.Tests;

/// <summary>Creates minimal in-memory binary documents for converter tests.</summary>
internal static class TestDocumentFactory
{
    /// <summary>Creates a minimal DOCX with a Heading1 paragraph and a body paragraph.</summary>
    internal static MemoryStream CreateDocxStream(string? title = null)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            if (title is not null)
                doc.PackageProperties.Title = title;

            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("Test Heading"))),
                new W.Paragraph(
                    new W.Run(new W.Text("Hello World")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Creates a minimal XLSX with a single sheet containing a header row and one data row.</summary>
    internal static MemoryStream CreateXlsxStream()
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();

            var headerRow = new S.Row { RowIndex = 1 };
            headerRow.AppendChild(new S.Cell { CellReference = "A1", CellValue = new S.CellValue("Name") });
            headerRow.AppendChild(new S.Cell { CellReference = "B1", CellValue = new S.CellValue("Score") });
            sheetData.AppendChild(headerRow);

            var dataRow = new S.Row { RowIndex = 2 };
            dataRow.AppendChild(new S.Cell { CellReference = "A2", CellValue = new S.CellValue("Alice") });
            dataRow.AppendChild(new S.Cell { CellReference = "B2", CellValue = new S.CellValue("100") });
            sheetData.AppendChild(dataRow);

            worksheetPart.Worksheet = new S.Worksheet(sheetData);
            worksheetPart.Worksheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.AppendChild(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });
            workbookPart.Workbook.Save();
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Creates a minimal PPTX (assembled as a ZIP) with one slide containing a title shape.</summary>
    internal static MemoryStream CreatePptxStream()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "ppt/presentation.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>
                </p:presentation>
                """);
            AddEntry(archive, "ppt/_rels/presentation.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "ppt/slides/slide1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <p:cSld><p:spTree>
                  <p:nvGrpSpPr>
                    <p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/>
                  </p:nvGrpSpPr>
                  <p:grpSpPr>
                    <a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/>
                    <a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm>
                  </p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="2" name="Title 1"/>
                      <p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr>
                      <p:nvPr><p:ph type="title"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/><a:lstStyle/>
                      <a:p><a:r><a:t>Test Slide Title</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                </p:spTree></p:cSld>
                </p:sld>
                """);
            AddEntry(archive, "ppt/slides/_rels/slide1.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>
                """);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Creates a minimal EPUB (assembled as a ZIP) with one chapter.</summary>
    internal static MemoryStream CreateEpubStream()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // mimetype must be the first entry and stored without compression
            var mimetypeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimetypeEntry.Open())) w.Write("application/epub+zip");

            AddEntry(archive, "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
            AddEntry(archive, "OEBPS/content.opf",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="BookId">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Test Book</dc:title>
                    <dc:identifier id="BookId">test-book-001</dc:identifier>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>
                    <item id="chapter1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                  </manifest>
                  <spine toc="ncx">
                    <itemref idref="chapter1"/>
                  </spine>
                </package>
                """);
            AddEntry(archive, "OEBPS/toc.ncx",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
                  <head><meta name="dtb:uid" content="test-book-001"/></head>
                  <docTitle><text>Test Book</text></docTitle>
                  <navMap/>
                </ncx>
                """);
            AddEntry(archive, "OEBPS/chapter1.xhtml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml">
                <head><title>Chapter 1</title></head>
                <body><h1>Hello EPUB</h1><p>Chapter content here.</p></body>
                </html>
                """);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Creates a minimal single-page PDF with no content (blank page).</summary>
    internal static MemoryStream CreatePdfStream()
    {
        string[] parts =
        [
            "%PDF-1.4\n",
            "1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n",
            "2 0 obj\n<</Type /Pages /Kids [3 0 R] /Count 1>>\nendobj\n",
            "3 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]>>\nendobj\n",
        ];

        // Compute byte offsets for each object (objects 1, 2, 3 — index 0 is the header)
        var offsets = new int[parts.Length - 1];
        var pos = Encoding.ASCII.GetByteCount(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            offsets[i - 1] = pos;
            pos += Encoding.ASCII.GetByteCount(parts[i]);
        }

        var xrefOffset = pos;
        var xref = new StringBuilder();
        xref.Append("xref\n");
        xref.Append($"0 {parts.Length}\n");
        xref.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            xref.Append($"{offset:D10} 00000 n \n");
        xref.Append($"trailer\n<</Size {parts.Length} /Root 1 0 R>>\n");
        xref.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        return new MemoryStream(Encoding.ASCII.GetBytes(string.Concat(parts) + xref));
    }

    /// <summary>Creates a minimal 1×1 pixel, 24-bit BMP image.</summary>
    internal static MemoryStream CreateBmpStream()
    {
        // BMP file header (14 bytes) + BITMAPINFOHEADER (40 bytes) + 1 row padded to 4 bytes = 58 bytes total
        byte[] bmp =
        [
            0x42, 0x4D,             // Signature: "BM"
            0x3A, 0x00, 0x00, 0x00, // File size: 58 bytes
            0x00, 0x00, 0x00, 0x00, // Reserved
            0x36, 0x00, 0x00, 0x00, // Pixel data offset: 54
            0x28, 0x00, 0x00, 0x00, // DIB header size: 40
            0x01, 0x00, 0x00, 0x00, // Width: 1 px
            0x01, 0x00, 0x00, 0x00, // Height: 1 px
            0x01, 0x00,             // Color planes: 1
            0x18, 0x00,             // Bits per pixel: 24
            0x00, 0x00, 0x00, 0x00, // Compression: BI_RGB
            0x04, 0x00, 0x00, 0x00, // Image size: 4 bytes (1 row × 4 bytes padded)
            0xC4, 0x0E, 0x00, 0x00, // X pixels/meter
            0xC4, 0x0E, 0x00, 0x00, // Y pixels/meter
            0x00, 0x00, 0x00, 0x00, // Colors in table: 0
            0x00, 0x00, 0x00, 0x00, // Important colors: 0
            0xFF, 0x00, 0x00, 0x00, // Pixel (B=255, G=0, R=0) + 1 padding byte
        ];
        return new MemoryStream(bmp);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
