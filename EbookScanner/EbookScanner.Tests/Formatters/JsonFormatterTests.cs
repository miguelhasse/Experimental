using System.Text.Json;
using EbookScanner.Core.Formatters;
using EbookScanner.Core.Models;

namespace EbookScanner.Tests.Formatters;

public sealed class JsonFormatterTests
{
    private static readonly BookMetadata SampleBook = new(
        FilePath: "/library/book.epub",
        FileName: "book.epub",
        Format: "EPUB",
        FileSizeBytes: 512_000,
        Title: "The Pragmatic Programmer",
        Authors: ["David Thomas", "Andrew Hunt"],
        Publisher: "Addison-Wesley",
        Language: "en",
        PublishedDate: new DateTimeOffset(2019, 9, 13, 0, 0, 0, TimeSpan.Zero));

    private static ScanResult MakeScanResult(params BookMetadata[] books) =>
        new("/library", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), books);

    [Fact]
    public void Format_ProducesValidJson()
    {
        var result = MakeScanResult(SampleBook);
        var output = new JsonFormatter().Format(result);

        var doc = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Format_ContainsExpectedFields()
    {
        var result = MakeScanResult(SampleBook);
        var output = new JsonFormatter().Format(result);

        var doc = JsonDocument.Parse(output).RootElement;
        Assert.True(doc.TryGetProperty("scannedDirectory", out _));
        Assert.True(doc.TryGetProperty("scannedAt", out _));
        Assert.True(doc.TryGetProperty("books", out var books));
        Assert.Equal(1, books.GetArrayLength());
    }

    [Fact]
    public void Format_BookFields_UseCamelCase()
    {
        var result = MakeScanResult(SampleBook);
        var output = new JsonFormatter().Format(result);

        var book = JsonDocument.Parse(output).RootElement
            .GetProperty("books")[0];

        Assert.Equal("The Pragmatic Programmer", book.GetProperty("title").GetString());
        Assert.Equal("EPUB", book.GetProperty("format").GetString());
        Assert.Equal(2, book.GetProperty("authors").GetArrayLength());
    }

    [Fact]
    public void Format_NullFields_AreOmitted()
    {
        var minimal = new BookMetadata("/lib/x.pdf", "x.pdf", "PDF", 1024);
        var result = MakeScanResult(minimal);
        var output = new JsonFormatter().Format(result);

        var book = JsonDocument.Parse(output).RootElement.GetProperty("books")[0];
        Assert.False(book.TryGetProperty("title", out _));
        Assert.False(book.TryGetProperty("authors", out _));
        Assert.False(book.TryGetProperty("isbn", out _));
    }

    [Fact]
    public void Format_EmptyResult_HasEmptyBooksArray()
    {
        var result = MakeScanResult();
        var output = new JsonFormatter().Format(result);

        var doc = JsonDocument.Parse(output).RootElement;
        Assert.Equal(0, doc.GetProperty("books").GetArrayLength());
    }
}
