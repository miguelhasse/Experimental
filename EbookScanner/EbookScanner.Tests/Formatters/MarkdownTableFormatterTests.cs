using EbookScanner.Core.Formatters;
using EbookScanner.Core.Models;

namespace EbookScanner.Tests.Formatters;

public sealed class MarkdownTableFormatterTests
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
    public void Format_EmptyResult_ContainsHeaderAndEmptyTable()
    {
        var result = MakeScanResult();
        var output = new MarkdownTableFormatter().Format(result);

        Assert.Contains("# Ebook Catalog", output);
        Assert.Contains("**Directory:** /library", output);
        Assert.Contains("**Total:** 0 books", output);
        // Header row present
        Assert.Contains("| Name |", output);
        // No data rows beyond header + separator
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tableLines = lines.Where(l => l.TrimStart().StartsWith('|')).ToList();
        Assert.Equal(2, tableLines.Count); // header + separator only
    }

    [Fact]
    public void Format_DefaultColumns_SixColumns()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownTableFormatter().Format(result);

        // Default: name, location, format, authors, published, size
        Assert.Contains("| Name |", output);
        Assert.Contains("| Location |", output);
        Assert.Contains("| Format |", output);
        Assert.Contains("| Authors |", output);
        Assert.Contains("| Published |", output);
        Assert.Contains("| Size |", output);

        // Non-default columns should not appear
        Assert.DoesNotContain("| Publisher |", output);
        Assert.DoesNotContain("| Pages |", output);
    }

    [Fact]
    public void Format_DefaultColumns_DataRowContainsValues()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownTableFormatter().Format(result);

        Assert.Contains("Clean Code", output);
        Assert.Contains("/library/book.pdf", output);
        Assert.Contains("PDF", output);
        Assert.Contains("Robert C. Martin", output);
        Assert.Contains("2008-08-01", output);
        Assert.Contains("4.0 MB", output);
    }

    [Fact]
    public void Format_CustomColumns_OnlyRequestedColumnsPresent()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownTableFormatter(["name", "publisher", "pages"]).Format(result);

        Assert.Contains("| Name |", output);
        Assert.Contains("| Publisher |", output);
        Assert.Contains("| Pages |", output);
        Assert.Contains("Prentice Hall", output);
        Assert.Contains("431", output);

        Assert.DoesNotContain("| Location |", output);
        Assert.DoesNotContain("| Authors |", output);
        Assert.DoesNotContain("| Size |", output);
    }

    [Fact]
    public void Format_AllColumns_AllHeadersPresent()
    {
        var result = MakeScanResult(SamplePdf);
        var output = new MarkdownTableFormatter(MarkdownTableFormatter.ValidColumns).Format(result);

        foreach (var col in MarkdownTableFormatter.ValidColumns)
        {
            var header = col switch
            {
                "name"        => "Name",
                "location"    => "Location",
                "format"      => "Format",
                "authors"     => "Authors",
                "publisher"   => "Publisher",
                "language"    => "Language",
                "isbn"        => "ISBN",
                "published"   => "Published",
                "modified"    => "Modified",
                "pages"       => "Pages",
                "tags"        => "Tags",
                "size"        => "Size",
                "description" => "Description",
                _             => col,
            };
            Assert.Contains($"| {header} |", output);
        }
    }

    [Fact]
    public void Format_NullFields_RenderAsEmptyCell()
    {
        var result = MakeScanResult(MinimalEpub);
        var output = new MarkdownTableFormatter(["name", "authors", "publisher", "published"]).Format(result);

        // Should not contain "null" literally
        Assert.DoesNotContain("null", output, StringComparison.OrdinalIgnoreCase);

        // The row should still be present (minimal epub has no authors/publisher/published)
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tableLines = lines.Where(l => l.TrimStart().StartsWith('|')).ToList();
        Assert.Equal(3, tableLines.Count); // header + separator + 1 data row
    }

    [Fact]
    public void Format_LongDescription_TruncatedAt100Chars()
    {
        var longDesc = new string('a', 150);
        var book = SamplePdf with { Description = longDesc };
        var result = MakeScanResult(book);
        var output = new MarkdownTableFormatter(["description"]).Format(result);

        Assert.Contains("…", output);
        // The cell should not contain all 150 'a' characters
        Assert.DoesNotContain(new string('a', 101), output);
    }

    [Fact]
    public void Format_PipeInCellValue_IsEscaped()
    {
        var book = SamplePdf with { Title = "Pipes | and | More" };
        var result = MakeScanResult(book);
        var output = new MarkdownTableFormatter(["name"]).Format(result);

        Assert.Contains(@"Pipes \| and \| More", output);
    }

    [Fact]
    public void Format_NoTitle_UsesFilenameWithoutExtension()
    {
        var result = MakeScanResult(MinimalEpub);
        var output = new MarkdownTableFormatter(["name"]).Format(result);

        Assert.Contains("minimal", output);
        Assert.DoesNotContain(".epub", output.Split('\n')
            .First(l => l.TrimStart().StartsWith('|') && !l.Contains("Name") && !l.Contains("---")));
    }

    [Fact]
    public void Format_MultipleBooks_MultipleDataRows()
    {
        var result = MakeScanResult(SamplePdf, MinimalEpub);
        var output = new MarkdownTableFormatter().Format(result);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tableLines = lines.Where(l => l.TrimStart().StartsWith('|')).ToList();
        Assert.Equal(4, tableLines.Count); // header + separator + 2 data rows
    }
}
