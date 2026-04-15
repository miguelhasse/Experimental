using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Tests;

public sealed class EpubConverterParityTests : IDisposable
{
    private readonly MarkItDownService _service = new();

    public void Dispose() => _service.Dispose();

    private Task<DocumentConverterResult> ConvertStreamAsync(Stream stream, StreamInfo? streamInfo = null) =>
        _service.ConvertStreamAsync(stream, streamInfo, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EpubConversion_PrependsMetadataBeforeChapterContent()
    {
        await using var stream = TestDocumentFactory.CreateEpubStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.epub", Extension: ".epub"));

        var expectedPrefix = string.Join(
            Environment.NewLine,
            [
                "**Title:** Test Book",
                "**Authors:** Test Author",
                "**Language:** en",
                "**Description:** A test EPUB document for MarkItDown testing",
                "**Identifier:** test-book-id",
            ]);

        Assert.StartsWith(expectedPrefix, result.Markdown);
        Assert.Contains("Hello EPUB", result.Markdown);
    }

    [Fact]
    public async Task EpubConversion_UsesNavTitleAsHeading()
    {
        await using var stream = TestDocumentFactory.CreateEpubStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.epub", Extension: ".epub"));

        Assert.Contains("## Chapter One", result.Markdown);
    }

    [Fact]
    public async Task EpubConversion_DoesNotEmitRawXhtmlFilenameAsHeading()
    {
        await using var stream = TestDocumentFactory.CreateEpubStream();

        var result = await ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "book.epub", Extension: ".epub"));

        Assert.DoesNotContain("## chapter1.xhtml", result.Markdown);
    }
}
