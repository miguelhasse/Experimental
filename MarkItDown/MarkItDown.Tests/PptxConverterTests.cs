using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Tests;

public sealed class PptxConverterTests : IDisposable
{
    private readonly MarkItDownService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task PptxConversion_HandlesImagesChartsAndGroupedShapes()
    {
        await using var stream = TestDocumentFactory.CreateRichPptxStream();

        var result = await _service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "slides.pptx", Extension: ".pptx"),
            TestContext.Current.CancellationToken);

        Assert.Contains("<!-- Slide number: 1 -->", result.Markdown);
        Assert.Contains("# Test Slide Title", result.Markdown);
        Assert.Contains("Body text outside the group.", result.Markdown);
        Assert.Contains("![Sample image](image1.png)", result.Markdown);
        Assert.Contains("### Chart: Sales Chart", result.Markdown);
        Assert.Contains("| Category | Series 1 |", result.Markdown);
        Assert.Contains("| Q1 | 4 |", result.Markdown);
        Assert.Contains("Grouped text", result.Markdown);
    }
}
