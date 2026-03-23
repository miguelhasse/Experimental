using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

/// <summary>
/// End-to-end benchmarks that go through the full MarkItDownService pipeline
/// (MIME normalization, converter selection, and conversion) for several formats.
/// Useful for measuring the dispatch overhead and comparing formats head-to-head.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Service")]
public class ServiceBenchmarks
{
    private MarkItDownService _service = null!;

    // Pre-built payloads
    private byte[] _txtBytes = null!;
    private byte[] _htmlBytes = null!;
    private byte[] _csvBytes = null!;
    private byte[] _docxBytes = null!;
    private byte[] _xlsxBytes = null!;
    private byte[] _pptxBytes = null!;
    private byte[] _pdfBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _txtBytes = TestDataFactory.CreatePlainText(paragraphs: 20);
        _htmlBytes = TestDataFactory.CreateHtml(sections: 10);
        _csvBytes = TestDataFactory.CreateCsv(rows: 50, cols: 6);
        _docxBytes = TestDataFactory.CreateDocx(paragraphs: 20);
        _xlsxBytes = TestDataFactory.CreateXlsx(rows: 50, cols: 6);
        _pptxBytes = TestDataFactory.CreatePptx(slides: 5);
        _pdfBytes = TestDataFactory.CreatePdf(pages: 3);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "Plain text (dispatch + read)")]
    public async Task<DocumentConverterResult> PlainText()
    {
        await using var stream = new MemoryStream(_txtBytes);
        return await _service.ConvertStreamAsync(
            stream, new StreamInfo(Extension: ".txt", MimeType: "text/plain"));
    }

    [Benchmark(Description = "HTML (dispatch + HAP + ReverseMarkdown)")]
    public async Task<DocumentConverterResult> Html()
    {
        await using var stream = new MemoryStream(_htmlBytes);
        return await _service.ConvertStreamAsync(
            stream, new StreamInfo(Extension: ".html", MimeType: "text/html"));
    }

    [Benchmark(Description = "CSV (dispatch + CsvHelper + table builder)")]
    public async Task<DocumentConverterResult> Csv()
    {
        await using var stream = new MemoryStream(_csvBytes);
        return await _service.ConvertStreamAsync(
            stream, new StreamInfo(Extension: ".csv", MimeType: "text/csv"));
    }

    [Benchmark(Description = "DOCX (dispatch + OpenXml DOM)")]
    public async Task<DocumentConverterResult> Docx()
    {
        await using var stream = new MemoryStream(_docxBytes);
        return await _service.ConvertStreamAsync(
            stream,
            new StreamInfo(Extension: ".docx",
                MimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Benchmark(Description = "XLSX (dispatch + OpenXml SpreadsheetML)")]
    public async Task<DocumentConverterResult> Xlsx()
    {
        await using var stream = new MemoryStream(_xlsxBytes);
        return await _service.ConvertStreamAsync(
            stream,
            new StreamInfo(Extension: ".xlsx",
                MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Benchmark(Description = "PPTX (dispatch + OpenXml PresentationML)")]
    public async Task<DocumentConverterResult> Pptx()
    {
        await using var stream = new MemoryStream(_pptxBytes);
        return await _service.ConvertStreamAsync(
            stream,
            new StreamInfo(Extension: ".pptx",
                MimeType: "application/vnd.openxmlformats-officedocument.presentationml.presentation"));
    }

    [Benchmark(Description = "PDF (dispatch + PdfPig page extraction)")]
    public async Task<DocumentConverterResult> Pdf()
    {
        await using var stream = new MemoryStream(_pdfBytes);
        return await _service.ConvertStreamAsync(
            stream, new StreamInfo(Extension: ".pdf", MimeType: "application/pdf"));
    }
}
