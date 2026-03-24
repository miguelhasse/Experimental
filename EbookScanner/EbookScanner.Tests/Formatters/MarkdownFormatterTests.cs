using EbookScanner.Core.Formatters;
using EbookScanner.Core.Models;

namespace EbookScanner.Tests.Formatters;

public sealed class MarkdownFormatterTests
{
    private static readonly BookMetadata SamplePdf = new(
        FilePath: "/library/book.pdf",
        FileName: "book.pdf",
        Format: "PDF",
        FileSizeBytes: 4_194_304,
        Title: "Clean Code",
        Authors: ["Robert C. Martin"],
        Publisher: "Prentice Hall",
        Description: "A handbook of agile software craftsmanship.",
        Language: "en",
        Isbn: "978-0132350884",
        PublishedDate: new DateTimeOffset(2008, 8, 1, 0, 0, 0, TimeSpan.Zero),
        PageCount: 431,
        Tags: ["programming", "software engineering"]);

    private static readonly BookMetadata MinimalEpub = new(
        FilePath: "/library/minimal.epub",
        FileName: "minimal.epub",
        Format: "EPUB",
        FileSizeBytes: 102_400);

    private static ScanResult MakeScanResult(params BookMetadata[] books) =>
        new("/library", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), books);

    [Fact]
    public void Format_EmptyResult_ContainsCatalogHeader()
    {
        var result = MakeScanResult();
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("# Ebook Catalog", output);
        Assert.Contains("**Directory:** /library", output);
        Assert.Contains("**Total:** 0 books", output);
    }

    [Fact]
    public void Format_SingleBook_ContainsTitle()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("## Clean Code", output);
    }

    [Fact]
    public void Format_SingleBook_ContainsAllFields()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("Robert C. Martin", output);
        Assert.Contains("Prentice Hall", output);
        Assert.Contains("978-0132350884", output);
        Assert.Contains("431", output);
        Assert.Contains("2008-08-01", output);
        Assert.Contains("programming", output);
        Assert.Contains("4.0 MB", output);
    }

    [Fact]
    public void Format_MultipleBooks_ShowsFormatBreakdown()
    {
        var epub = SamplePdf with { FileName = "other.epub", Format = "EPUB" };
        var result = MakeScanResult(SamplePdf, epub);
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("PDF: 1", output);
        Assert.Contains("EPUB: 1", output);
    }

    [Fact]
    public void Format_BookWithNoTitle_UsesFileNameWithoutExtension()
    {
        var result = MakeScanResult(MinimalEpub);
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("## minimal", output);
    }

    [Fact]
    public void Format_LongDescription_IsTruncated()
    {
        var longDesc = new string('x', 250);
        var book = SamplePdf with { Description = longDesc };
        var result = MakeScanResult(book);
        var output = new MarkdownFormatter().Format(result);

        Assert.Contains("…", output);
    }
}
