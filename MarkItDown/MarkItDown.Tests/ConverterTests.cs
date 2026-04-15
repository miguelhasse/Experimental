using Azure;
using MarkItDown.Azure;
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

    [Fact]
    public void DocumentIntelligenceConverter_AcceptsSupportedFormats()
    {
        var converter = new DocumentIntelligenceConverter(
            "https://example.cognitiveservices.azure.com",
            new AzureKeyCredential("fake-key"));

        Assert.True(converter.Accepts(Stream.Null, new StreamInfo(Extension: ".pdf")));
        Assert.True(converter.Accepts(Stream.Null, new StreamInfo(Extension: ".docx")));
        Assert.True(converter.Accepts(Stream.Null, new StreamInfo(MimeType: "image/jpeg")));
        Assert.False(converter.Accepts(Stream.Null, new StreamInfo(Extension: ".csv")));
    }

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
    public async Task CsvConversion_HandlesShiftJISEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        var csv = "名前,年齢\r\n佐藤太郎,30\r\n三木英子,25";
        var bytes = shiftJis.GetBytes(csv);

        await using var stream = new MemoryStream(bytes);
        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "test.csv", Extension: ".csv", MimeType: "text/csv"));

        Assert.Contains("名前", result.Markdown);
        Assert.Contains("佐藤太郎", result.Markdown);
        Assert.Contains("三木英子", result.Markdown);
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
    public async Task RssConversion_FormatsLinkAsMarkdown()
    {
        const string rss =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Link Feed</title>
                <item>
                  <title>Click Me</title>
                  <link>https://example.com/click</link>
                  <description>Simple description</description>
                </item>
              </channel>
            </rss>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rss));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.rss", Extension: ".rss"));

        Assert.Contains("[Click Me](https://example.com/click)", result.Markdown);
    }

    [Fact]
    public async Task RssConversion_EmitsPublishedDate()
    {
        const string rss =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>Date Feed</title>
                <item>
                  <title>Dated Item</title>
                  <pubDate>Mon, 01 Jan 2024 00:00:00 +0000</pubDate>
                  <description>Has a date</description>
                </item>
              </channel>
            </rss>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rss));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.rss", Extension: ".rss"));

        Assert.Contains("Published on:", result.Markdown);
    }

    [Fact]
    public async Task RssConversion_StripsHtmlFromDescription()
    {
        const string rss =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>HTML Feed</title>
                <item>
                  <title>HTML Item</title>
                  <description>&lt;p&gt;Hello &lt;strong&gt;world&lt;/strong&gt;&lt;/p&gt;</description>
                </item>
              </channel>
            </rss>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rss));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.rss", Extension: ".rss"));

        Assert.Contains("Hello", result.Markdown);
        Assert.DoesNotContain("<p>", result.Markdown);
        Assert.DoesNotContain("<strong>", result.Markdown);
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
        Assert.Contains("[Entry One](https://example.com/entry/1)", result.Markdown);
    }

    [Fact]
    public async Task RssConversion_XmlExtension_IsDetectedByContent()
    {
        const string rss =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <title>XML Feed</title>
                <item>
                  <title>XML Item</title>
                </item>
              </channel>
            </rss>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rss));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "feed.xml", Extension: ".xml"));

        Assert.Equal("XML Feed", result.Title);
        Assert.Contains("## XML Item", result.Markdown);
    }

    [Theory]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    public async Task RssConversion_XmlMimeType_IsDetectedByContent(string mimeType)
    {
        const string atom =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>XML MIME Feed</title>
              <entry>
                <title>XML MIME Entry</title>
              </entry>
            </feed>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(atom));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(MimeType: mimeType));

        Assert.Equal("XML MIME Feed", result.Title);
        Assert.Contains("## XML MIME Entry", result.Markdown);
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

    [Fact]
    public async Task WikipediaConversion_ExtractsMainContent()
    {
        await using var stream = TestDocumentFactory.CreateWikipediaHtmlStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(Url: "https://en.wikipedia.org/wiki/Cat"));

        Assert.Contains("carnivorous mammal", result.Markdown);
        Assert.DoesNotContain("Navigation chrome", result.Markdown);
        Assert.DoesNotContain("Footer chrome", result.Markdown);
    }

    [Fact]
    public async Task WikipediaConversion_PrependsTitleHeading()
    {
        await using var stream = TestDocumentFactory.CreateWikipediaHtmlStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(Url: "https://en.wikipedia.org/wiki/Cat"));

        Assert.StartsWith("# Cat", result.Markdown);
        Assert.Equal("Cat", result.Title);
    }

    [Fact]
    public async Task WikipediaConversion_FallsBackOnGenericHtml()
    {
        await using var stream = TestDocumentFactory.CreateGenericHtmlStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(Url: "https://en.wikipedia.org/wiki/Anything"));

        Assert.Contains("generic content", result.Markdown);
    }

    [Fact]
    public void WikipediaConverter_RejectsSpoof_NotWikipediaOrg()
    {
        var converter = new WikipediaConverter();
        using var stream = new MemoryStream();
        Assert.False(converter.Accepts(stream, new StreamInfo(Url: "https://notwikipedia.org/wiki/Test")));
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


    [Fact]
    public async Task DocxConversion_UsesOutlineLevelForHeadings()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithOutlineHeading();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("# Outline Heading", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_EmitsBoldMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithBoldItalicRuns();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("**bold text**", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_EmitsItalicMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithBoldItalicRuns();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("_italic text_", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_EmitsBoldItalicMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithBoldItalicRuns();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("**_bold italic text_**", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_ResolvesCustomStyleInheritanceToHeading()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithCustomHeadingStyle();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("# Custom Heading", result.Markdown);
    }

    [Fact]
    public async Task DocxConversion_EmitsEmbeddedImageMarkdown()
    {
        await using var stream = TestDocumentFactory.CreateDocxStreamWithEmbeddedImage();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "document.docx", Extension: ".docx"));

        Assert.Contains("Before image", result.Markdown);
        Assert.Contains("After image", result.Markdown);
        Assert.Contains("![Sample image](data:image/bmp;base64,", result.Markdown);
    }    // ── XLSX ──────────────────────────────────────────────────────────────────

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

    // ── MOBI ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MobiConversion_ExtractsBookTitle()
    {
        await using var stream = TestDocumentFactory.CreateMobiStream(title: "My MOBI Book");

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.mobi", Extension: ".mobi"));

        Assert.Equal("My MOBI Book", result.Title);
    }

    [Fact]
    public async Task MobiConversion_ProducesMarkdownFromContent()
    {
        await using var stream = TestDocumentFactory.CreateMobiStream(
            bodyHtml: "<html><body><h1>Hello MOBI</h1><p>Test content.</p></body></html>");

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.mobi", Extension: ".mobi"));

        Assert.Contains("Hello MOBI", result.Markdown);
        Assert.Contains("Test content", result.Markdown);
    }

    [Fact]
    public void MobiConverter_AcceptsMobiExtension()
    {
        var converter = new MobiConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Extension: ".mobi")));
    }

    [Fact]
    public void MobiConverter_AcceptsAzwExtension()
    {
        var converter = new MobiConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Extension: ".azw")));
    }

    [Fact]
    public void MobiConverter_AcceptsMobiMimeType()
    {
        var converter = new MobiConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream,
            new StreamInfo(MimeType: "application/x-mobipocket-ebook")));
    }

    // ── CHM ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ChmConverter_AcceptsByChmExtension()
    {
        var converter = new ChmConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Extension: ".chm")));
    }

    [Fact]
    public void ChmConverter_AcceptsByMimeType()
    {
        var converter = new ChmConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream,
            new StreamInfo(MimeType: "application/vnd.ms-htmlhelp")));
    }

    [Fact]
    public void ChmConverter_AcceptsByItsfMagicBytes()
    {
        var converter = new ChmConverter();
        byte[] magic = [0x49, 0x54, 0x53, 0x46, 0x00, 0x00]; // "ITSF"...
        using var stream = new MemoryStream(magic);
        Assert.True(converter.Accepts(stream, new StreamInfo()));
    }

    [Fact]
    public void ChmConverter_RejectsNonChmFile()
    {
        var converter = new ChmConverter();
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03]);
        Assert.False(converter.Accepts(stream, new StreamInfo()));
    }

    [Fact]
    public async Task ChmConversion_ExtractsTitle()
    {
        await using var stream = TestDocumentFactory.CreateChmStream(title: "My CHM Book");

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.chm", Extension: ".chm"));

        Assert.Equal("My CHM Book", result.Title);
    }

    [Fact]
    public async Task ChmConversion_ProducesMarkdownFromHtml()
    {
        await using var stream = TestDocumentFactory.CreateChmStream(
            bodyHtml: "<html><body><h1>Hello CHM</h1><p>Help content.</p></body></html>");

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "help.chm", Extension: ".chm"));

        Assert.Contains("Hello CHM", result.Markdown);
        Assert.Contains("Help content", result.Markdown);
    }

    // ── CHM real-file integration ─────────────────────────────────────────────
    // These tests run against real Microsoft Press CHM files stored locally.
    // Each test skips automatically when the file is not present (e.g. in CI).

    private const string ChmBooksFolder = @"E:\Books\Microsoft Press";

    [Fact]
    public async Task ChmRealFile_SqlServerSystemTablesMap_ProducesNonEmptyMarkdown()
    {
        const string fileName = "SQL Server 2000 System Tables Map.chm";
        string path = Path.Combine(ChmBooksFolder, fileName);
        if (!File.Exists(path)) Assert.Skip($"Real CHM test file not available: {path}");

        await using var stream = File.OpenRead(path);
        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: fileName, Extension: ".chm"));

        Assert.False(string.IsNullOrWhiteSpace(result.Markdown),
            "Expected non-empty markdown but the converter returned an empty result.");
        Assert.True(result.Markdown.Length > 100,
            $"Expected substantial markdown but got only {result.Markdown.Length} chars.");
    }

    [Fact]
    public async Task ChmRealFile_DeployingDotNetApplications_ProducesNonEmptyMarkdown()
    {
        const string fileName = "Deploying .NET Applications Lifecycle Guide.chm";
        string path = Path.Combine(ChmBooksFolder, fileName);
        if (!File.Exists(path)) Assert.Skip($"Real CHM test file not available: {path}");

        await using var stream = File.OpenRead(path);
        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: fileName, Extension: ".chm"));

        Assert.False(string.IsNullOrWhiteSpace(result.Markdown),
            "Expected non-empty markdown but the converter returned an empty result.");
        Assert.True(result.Markdown.Length > 100,
            $"Expected substantial markdown but got only {result.Markdown.Length} chars.");
    }

    [Fact]
    public async Task ChmRealFile_MicrosoftDotNetRemoting_ProducesNonEmptyMarkdown()
    {
        const string fileName = "Microsoft .NET Remoting.chm";
        string path = Path.Combine(ChmBooksFolder, fileName);
        if (!File.Exists(path)) Assert.Skip($"Real CHM test file not available: {path}");

        await using var stream = File.OpenRead(path);
        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: fileName, Extension: ".chm"));

        Assert.False(string.IsNullOrWhiteSpace(result.Markdown),
            "Expected non-empty markdown but the converter returned an empty result.");
        Assert.True(result.Markdown.Length > 100,
            $"Expected substantial markdown but got only {result.Markdown.Length} chars.");
    }

    // ── BingSerpConverter ─────────────────────────────────────────────────────

    [Fact]
    public void BingSerpConverter_RejectsNonBingUrl()
    {
        var converter = new BingSerpConverter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<html></html>"));
        Assert.False(converter.Accepts(stream,
            new StreamInfo(MimeType: "text/html", Extension: ".html", Url: "https://www.example.com")));
    }

    [Fact]
    public void BingSerpConverter_AcceptsBingSearchUrl()
    {
        var converter = new BingSerpConverter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<html></html>"));
        Assert.True(converter.Accepts(stream,
            new StreamInfo(MimeType: "text/html", Extension: ".html", Url: "https://www.bing.com/search?q=hello")));
    }

    [Fact]
    public void BingSerpConverter_RejectsBingUrlWithoutHtmlMime()
    {
        var converter = new BingSerpConverter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        Assert.False(converter.Accepts(stream,
            new StreamInfo(MimeType: "application/octet-stream", Url: "https://www.bing.com/search?q=hello")));
    }

    [Fact]
    public async Task BingSerpConverter_AcceptsBingUrl_ProducesSearchHeader()
    {
        const string html = """
            <html>
            <head><title>hello - Bing</title></head>
            <body>
            <ol>
                <li class="b_algo">
                    <h2><a href="https://example.com">Example Site</a></h2>
                    <p>A short description of the result.</p>
                </li>
            </ol>
            </body>
            </html>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(MimeType: "text/html", Extension: ".html", Url: "https://www.bing.com/search?q=hello"));

        Assert.Contains("A Bing search for 'hello'", result.Markdown);
        Assert.Contains("Example Site", result.Markdown);
        Assert.Equal("hello - Bing", result.Title);
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

    [Fact]
    public async Task AudioConverter_AcceptsWavExtension()
    {
        // Minimal valid WAV: RIFF header for 0-sample file
        var wavBytes = new byte[]
        {
            0x52,0x49,0x46,0x46, // "RIFF"
            0x24,0x00,0x00,0x00, // file size - 8 = 36
            0x57,0x41,0x56,0x45, // "WAVE"
            0x66,0x6D,0x74,0x20, // "fmt "
            0x10,0x00,0x00,0x00, // chunk size = 16
            0x01,0x00,           // PCM format
            0x01,0x00,           // 1 channel
            0x44,0xAC,0x00,0x00, // 44100 Hz
            0x88,0x58,0x01,0x00, // byte rate
            0x02,0x00,           // block align
            0x10,0x00,           // bits per sample = 16
            0x64,0x61,0x74,0x61, // "data"
            0x00,0x00,0x00,0x00  // data chunk size = 0
        };

        await using var stream = new MemoryStream(wavBytes);

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "sample.wav", Extension: ".wav", MimeType: "audio/x-wav"));

        Assert.NotNull(result);
        Assert.NotNull(result.Markdown);
    }

    // ── Jupyter Notebook ──────────────────────────────────────────────────────

    [Fact]
    public async Task IpynbConversion_ProducesMarkdown()
    {
        const string notebook = """
            {
              "nbformat": 4,
              "nbformat_minor": 5,
              "metadata": {},
              "cells": [
                {
                  "cell_type": "markdown",
                  "source": ["# Test Title\n", "\n", "Some intro text."]
                },
                {
                  "cell_type": "code",
                  "source": ["print('hello')"]
                }
              ]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(notebook));

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "notebook.ipynb", Extension: ".ipynb"));

        Assert.Contains("# Test Title", result.Markdown);
        Assert.Contains("print('hello')", result.Markdown);
        Assert.Equal("Test Title", result.Title);
    }
    // ── OutlookMsgConverter ───────────────────────────────────────────────────

    [Fact]
    public void OutlookMsgConverter_AcceptsMsgExtension()
    {
        var converter = new OutlookMsgConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream, new StreamInfo(Extension: ".msg")));
    }

    [Fact]
    public void OutlookMsgConverter_AcceptsOutlookMimeType()
    {
        var converter = new OutlookMsgConverter();
        using var stream = new MemoryStream();
        Assert.True(converter.Accepts(stream,
            new StreamInfo(MimeType: "application/vnd.ms-outlook")));
    }

    [Fact]
    public void OutlookMsgConverter_AcceptsOleSignatureBytes()
    {
        var converter = new OutlookMsgConverter();
        byte[] oleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];
        using var stream = new MemoryStream(oleSignature);
        Assert.True(converter.Accepts(stream, new StreamInfo()));
    }

    [Fact]
    public void OutlookMsgConverter_RejectsNonMsgFile()
    {
        var converter = new OutlookMsgConverter();
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        Assert.False(converter.Accepts(stream, new StreamInfo()));
    }

    [Fact]
    public void OutlookMsgConverter_IsRegisteredInService()
    {
        Assert.Contains(_service.Converters,
            r => r.Converter is OutlookMsgConverter);
    }
}