using MarkItDown.Core;
using MarkItDown.Core.Converters;
using MarkItDown.Core.Exceptions;
using MarkItDown.Core.Models;
using System.IO.Compression;
using System.Text;

namespace MarkItDown.Tests;

public sealed class ConverterTests : IDisposable
{
    private readonly MarkItDownService _service = new();

    public void Dispose() => _service.Dispose();

    private Task<DocumentConverterResult> ConvertAsync(string source, StreamInfo? streamInfo = null) =>
        _service.ConvertAsync(source, streamInfo, TestContext.Current.CancellationToken);

    private Task<DocumentConverterResult> ConvertStreamAsync(Stream stream, StreamInfo? streamInfo = null) =>
        _service.ConvertStreamAsync(stream, streamInfo, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PlainTextConversion_ReturnsSourceText()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "sample.txt", Extension: ".txt", MimeType: "text/plain"));

        Assert.Equal("hello world", result.Markdown);
    }

    [Fact]
    public async Task HtmlConversion_ProducesMarkdown()
    {
        const string html = "<html><head><title>Test</title></head><body><h1>Hello</h1><p>World</p></body></html>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "sample.html", Extension: ".html", MimeType: "text/html"));

        Assert.Contains("# Hello", result.Markdown);
        Assert.Contains("World", result.Markdown);
        Assert.Equal("Test", result.Title);
    }

    [Fact]
    public async Task CsvConversion_ProducesMarkdownTable()
    {
        const string csv = "Name,Value\r\nA,1\r\nB,2";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "sample.csv", Extension: ".csv", MimeType: "text/csv"));

        Assert.Contains("| Name | Value |", result.Markdown);
        Assert.Contains("| A | 1 |", result.Markdown);
        Assert.Contains("| B | 2 |", result.Markdown);
    }

    [Fact]
    public async Task PlainText_JsonExtension_IsAccepted()
    {
        await using var stream = new MemoryStream("""{"key":"value"}"""u8.ToArray());

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "data.json", Extension: ".json"));

        Assert.Contains("key", result.Markdown);
    }

    [Fact]
    public async Task UnsupportedFormat_ThrowsUnsupportedFormatException()
    {
        await using var stream = new MemoryStream([0x00, 0x01, 0x02]);

        await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            ConvertStreamAsync(
                stream,
                new StreamInfo(Extension: ".xyz", MimeType: "application/x-unknown")));
    }

    [Fact]
    public async Task EmptyHtml_ReturnsEmptyMarkdown()
    {
        await using var stream = new MemoryStream(""u8.ToArray());

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "empty.html", Extension: ".html", MimeType: "text/html"));

        Assert.NotNull(result.Markdown);
    }

    [Fact]
    public async Task CsvWithSpecialChars_EscapesPipeInCells()
    {
        const string csv = "Name,Value\r\nA|B,1\r\nC,2";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "test.csv", Extension: ".csv"));

        Assert.Contains(@"A\|B", result.Markdown);
    }

    [Fact]
    public async Task DataUri_TextPlain_IsConverted()
    {
        var result = await ConvertAsync("data:text/plain;base64,SGVsbG8gV29ybGQ=");

        Assert.Equal("Hello World", result.Markdown);
    }

    [Fact]
    public async Task StreamInfo_Merge_PreservesExistingValues()
    {
        var original = new StreamInfo(MimeType: "text/html", Extension: ".html");
        var overlay = new StreamInfo(FileName: "page.html");

        var merged = original.Merge(overlay);

        Assert.Equal("text/html", merged.MimeType);
        Assert.Equal(".html", merged.Extension);
        Assert.Equal("page.html", merged.FileName);
    }

    [Fact]
    public async Task StreamInfo_Merge_OverlayTakesPrecedence()
    {
        var original = new StreamInfo(MimeType: "text/plain");
        var overlay = new StreamInfo(MimeType: "text/html");

        var merged = original.Merge(overlay);

        Assert.Equal("text/html", merged.MimeType);
        await Task.CompletedTask;
    }

    [Fact]
    public void Converters_Property_ReturnsSortedByPriority()
    {
        var converters = _service.Converters;

        for (var i = 1; i < converters.Count; i++)
        {
            Assert.True(converters[i - 1].Priority <= converters[i].Priority,
                $"Converters should be sorted by priority, but {converters[i - 1].Converter.Name} ({converters[i - 1].Priority}) > {converters[i].Converter.Name} ({converters[i].Priority})");
        }
    }

    [Fact]
    public void Service_ImplementsIDisposable()
    {
        var service = new MarkItDownService();
        service.Dispose(); // should not throw
    }

    [Fact]
    public async Task PlainText_BinaryMime_NotAcceptedAsText()
    {
        await using var stream = new MemoryStream([0x00, 0x01, 0x02]);

        // application/octet-stream with charset should NOT be accepted by PlainTextConverter
        await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            ConvertStreamAsync(
                stream,
                new StreamInfo(MimeType: "application/octet-stream", Charset: "utf-8")));
    }

    // ── RSS ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RssConversion_ProducesMarkdownWithFeedTitleAndItems()
    {
        const string rss =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Test Feed</title>
                <description>A feed for testing</description>
                <item>
                  <title>Item One</title>
                  <link>https://example.com/1</link>
                  <description>First item description</description>
                </item>
              </channel>
            </rss>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rss));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.rss", Extension: ".rss"));

        Assert.Equal("Test Feed", result.Title);
        Assert.Contains("# Test Feed", result.Markdown);
        Assert.Contains("## Item One", result.Markdown);
        Assert.Contains("https://example.com/1", result.Markdown);
        Assert.Contains("First item description", result.Markdown);
    }

    [Fact]
    public async Task RssConversion_AtomFeed_ProducesMarkdown()
    {
        const string atom =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Feed</title>
              <entry>
                <title>Entry One</title>
                <link href="https://example.com/entry/1"/>
                <summary>Entry summary text</summary>
              </entry>
            </feed>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(atom));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.atom", Extension: ".atom"));

        Assert.Contains("# Atom Feed", result.Markdown);
        Assert.Contains("## Entry One", result.Markdown);
    }

    // ── ZIP ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZipConversion_ExtractsTextFileContent()
    {
        await using var stream = CreateZipStream(archive =>
        {
            AddZipTextEntry(archive, "readme.txt", "Hello ZIP");
        });

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "archive.zip", Extension: ".zip"));

        Assert.Contains("## File: readme.txt", result.Markdown);
        Assert.Contains("Hello ZIP", result.Markdown);
    }

    [Fact]
    public async Task ZipConversion_SkipsUnsupportedEntries()
    {
        await using var stream = CreateZipStream(archive =>
        {
            AddZipTextEntry(archive, "note.txt", "Readable text");
            var entry = archive.CreateEntry("data.xyz");
            using var w = entry.Open();
            w.Write([0x00, 0x01, 0x02, 0xFF], 0, 4);
        });

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "archive.zip", Extension: ".zip"));

        Assert.Contains("Readable text", result.Markdown);
    }

    // ── Wikipedia ─────────────────────────────────────────────────────────────

    [Fact]
    public void WikipediaConverter_AcceptsWikipediaUrl()
    {
        var converter = new WikipediaConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Url: "https://en.wikipedia.org/wiki/Test")));
    }

    [Fact]
    public void WikipediaConverter_RejectsNonWikipediaUrl()
    {
        var converter = new WikipediaConverter();
        using var stream = new MemoryStream();
        Assert.False(converter.Accepts(stream, new StreamInfo(Url: "https://www.google.com/")));
    }

    [Fact]
    public async Task WikipediaConversion_ProducesMarkdownFromHtmlStream()
    {
        const string html =
            """
            <html>
            <head><title>Test Article - Wikipedia</title></head>
            <body><h1>Test Article</h1><p>Article content.</p></body>
            </html>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(Url: "https://en.wikipedia.org/wiki/Test_Article"));

        Assert.Contains("Test Article", result.Markdown);
        Assert.Equal("Test Article - Wikipedia", result.Title);
    }

    // ── YouTube ───────────────────────────────────────────────────────────────

    [Fact]
    public void YouTubeConverter_AcceptsYoutubeComUrl()
    {
        var converter = new YouTubeConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ")));
    }

    [Fact]
    public void YouTubeConverter_AcceptsYoutuBeShortUrl()
    {
        var converter = new YouTubeConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Url: "https://youtu.be/dQw4w9WgXcQ")));
    }

    [Fact]
    public void YouTubeConverter_RejectsNonYoutubeUrl()
    {
        var converter = new YouTubeConverter();
        using var stream = new MemoryStream();
        Assert.False(converter.Accepts(stream, new StreamInfo(Url: "https://www.vimeo.com/video/12345")));
    }

    [Fact]
    public async Task YouTubeConversion_ExtractsTitleAndDescription()
    {
        const string html =
            """
            <html>
            <head>
              <meta property="og:title" content="My Video Title"/>
              <meta property="og:description" content="Video description text"/>
            </head>
            <body></body>
            </html>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(Url: "https://www.youtube.com/watch?v=test123"));

        Assert.Equal("My Video Title", result.Title);
        Assert.Contains("# My Video Title", result.Markdown);
        Assert.Contains("Video description text", result.Markdown);
    }

    // ── DOCX ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DocxConversion_ProducesHeadingMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateDocxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("# Test Heading", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_ProducesParagraphText()
    {
        await using var stream = TestDocumentFactory.CreateDocxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("Hello World", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_ExtractsDocumentTitle()
    {
        await using var stream = TestDocumentFactory.CreateDocxStream(title: "My Document");

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Equal("My Document", result.Title);
    }

    // ── XLSX ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task XlsxConversion_ProducesMarkdownTable()
    {
        await using var stream = TestDocumentFactory.CreateXlsxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "workbook.xlsx", Extension: ".xlsx"));

        Assert.Contains("| Name | Score |", result.Markdown);
        Assert.Contains("| Alice | 100 |", result.Markdown);
    }

    [Fact]
    public async Task XlsxConversion_IncludesSheetName()
    {
        await using var stream = TestDocumentFactory.CreateXlsxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "workbook.xlsx", Extension: ".xlsx"));

        Assert.Contains("## Sheet1", result.Markdown);
    }

    // ── PPTX ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PptxConversion_IncludesSlideNumberComment()
    {
        await using var stream = TestDocumentFactory.CreatePptxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "slides.pptx", Extension: ".pptx"));

        Assert.Contains("<!-- Slide number: 1 -->", result.Markdown);
    }

    [Fact]
    public async Task PptxConversion_ExtractsTitleShape()
    {
        await using var stream = TestDocumentFactory.CreatePptxStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "slides.pptx", Extension: ".pptx"));

        Assert.Contains("# Test Slide Title", result.Markdown);
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PdfConversion_ProducesPageSectionHeading()
    {
        await using var stream = TestDocumentFactory.CreatePdfStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.pdf", Extension: ".pdf"));

        Assert.Contains("## Page 1", result.Markdown);
    }

    // ── Image ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImageConversion_BmpProducesMetadataMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateBmpStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.bmp", Extension: ".bmp", MimeType: "image/bmp"));

        Assert.NotNull(result);
    }

    // ── EPUB ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EpubConversion_ExtractsBookTitle()
    {
        await using var stream = TestDocumentFactory.CreateEpubStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.epub", Extension: ".epub"));

        Assert.Equal("Test Book", result.Title);
    }

    [Fact]
    public async Task EpubConversion_ProducesMarkdownFromChapterContent()
    {
        await using var stream = TestDocumentFactory.CreateEpubStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.epub", Extension: ".epub"));

        Assert.Contains("Hello EPUB", result.Markdown);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MemoryStream CreateZipStream(Action<ZipArchive> configure)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            configure(archive);
        ms.Position = 0;
        return ms;
    }

    private static void AddZipTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

